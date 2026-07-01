using Microsoft.Extensions.Logging;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Deals.Application;

/// <summary>
/// DJ4 commit seam (deals-domain-model §7). Flips a <see cref="Deal"/> to
/// <see cref="DealStatus.Confirmed"/> and runs the two cross-aggregate side effects — upsert the
/// <see cref="DealMatchMemory"/>, then write the deal-sourced <c>price_observation</c> and link it — as
/// <b>separate transactions</b> (Intake's resumable-commit discipline, ADR-010). Shared by the review UI
/// (P5-8) and the auto-confirm worker (P5-6).
///
/// <para><b>Resumable (the core correctness rule, DD2).</b> Each aggregate mutation is saved before the
/// next side effect runs, so a mid-confirm crash never double-writes. Re-driving a first-time confirm is
/// idempotent: the state flip is skipped once the deal is already <see cref="DealStatus.Confirmed"/>, the
/// memory upsert is idempotent on <c>(household, store, normalized_name)</c>, and the observation is
/// written only while <see cref="Deal.CommittedPriceObservationId"/> is null — a re-drive links only the
/// piece not yet done.</para>
///
/// <para><b>Correct = supersede, never edit (DM-17/R1).</b> A correction re-resolves the product, writes a
/// <b>new</b> append-only observation row, repoints the memory, and updates
/// <see cref="Deal.CommittedPriceObservationId"/> to the new row — the prior observation stays as history.</para>
///
/// <para><b>Past-window confirm still writes (backfill, DD14).</b> The aggregate does not gate on the
/// clock, so an expired deal's observation is still recorded (price history); it simply never reads as
/// "active".</para>
/// </summary>
public sealed class ConfirmDeal(
    IDealRepository deals,
    IDealMatchMemoryRepository memories,
    ICatalogProductReader products,
    IPriceObservationWriter observations,
    IClock clock,
    ITenantContext tenant,
    ILogger<ConfirmDeal> logger)
{
    public static readonly Error UnknownProduct = Error.Custom(
        "Deals.ConfirmDeal.UnknownProduct",
        "The resolved product does not exist in this household's catalog.");

    public static readonly Error CommitFailed = Error.Custom(
        "Deals.ConfirmDeal.CommitFailed",
        "A cross-context side effect failed mid-confirm; the confirm is resumable on retry.");

    /// <summary>User confirm (DJ4): the reviewer resolves the (possibly unchanged) match. Valid from Pending.</summary>
    public Task<Result> ConfirmAsync(DealId dealId, Guid productId, Guid reviewedByUserId, CancellationToken ct = default) =>
        ResolveAsync(dealId, productId, reviewedByUserId, supersede: false, ct);

    /// <summary>
    /// Memory auto-confirm (P5-6 worker path): the same side effects with <c>reviewed_by_user_id = null</c>.
    /// Valid from Pending; idempotent on re-pull once the observation is linked (§7).
    /// </summary>
    public Task<Result> AutoConfirmAsync(DealId dealId, Guid productId, CancellationToken ct = default) =>
        ResolveAsync(dealId, productId, reviewedByUserId: null, supersede: false, ct);

    /// <summary>
    /// Correct (DJ4 edge): re-resolve to a different product on a Pending or already-Confirmed deal and
    /// <b>supersede</b> — a new observation row + repointed memory + updated committed id.
    /// </summary>
    public Task<Result> CorrectAsync(DealId dealId, Guid productId, Guid reviewedByUserId, CancellationToken ct = default) =>
        ResolveAsync(dealId, productId, reviewedByUserId, supersede: true, ct);

    private async Task<Result> ResolveAsync(
        DealId dealId, Guid productId, Guid? reviewedByUserId, bool supersede, CancellationToken ct)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var deal = await deals.FindAsync(dealId, ct);
        if (deal is null)
            return Error.NotFound;

        // Validate the resolved product before any write — a dangling ref would poison memory + price history.
        if (!await products.ExistsAsync(productId, ct))
        {
            logger.LogWarning(
                "Confirm deal {DealId} rejected — resolved product {ProductId} is not a live catalog product.",
                dealId.Value, productId);
            return UnknownProduct;
        }

        try
        {
            // ── Transaction A: flip the deal's state (idempotent by the aggregate's status guard). ──
            var flip = FlipState(deal, productId, reviewedByUserId, supersede);
            if (flip.IsFailure)
                return flip.Error;
            if (flip.Value) // the aggregate actually transitioned this call → persist state (+ raise DealConfirmed)
                await deals.SaveChangesAsync(ct);

            var resolvedProduct = deal.ProductId!.Value;

            // ── Transaction B: upsert / repoint the match memory (idempotent on the key). ──
            await UpsertMemoryAsync(deal, resolvedProduct, reviewedByUserId, ct);

            // ── Transaction C: write the deal observation + link it. ──
            // Confirm/AutoConfirm: resumable — write only while not yet linked (a re-drive skips a linked deal).
            // Correct: always supersede — a new append-only row + repointed committed id.
            if (supersede || deal.CommittedPriceObservationId is null)
            {
                var observationId = await observations.RecordObservationAsync(
                    resolvedProduct,
                    deal.Price,
                    deal.Quantity,
                    deal.UnitId,
                    deal.StoreId,
                    deal.ValidityWindow.ValidFrom,
                    deal.ValidityWindow.ValidTo,
                    deal.Id.Value,
                    reviewedByUserId,
                    clock.UtcNow,
                    ct);

                var link = deal.LinkObservation(observationId, clock);
                if (link.IsFailure)
                    return link.Error;
                await deals.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Confirm deal {DealId} failed mid-commit; retry is resumable.", dealId.Value);
            return CommitFailed;
        }

        logger.LogInformation(
            "Deal {DealId} confirmed to product {ProductId} (autoMatched={AutoMatched}, corrected={Corrected}).",
            dealId.Value, deal.ProductId!.Value, deal.AutoMatched, supersede);
        return Result.Success();
    }

    /// <summary>
    /// Applies the aggregate transition. Returns whether the aggregate actually changed state this call
    /// (so only a real transition triggers a save + a re-drive of an already-confirmed deal is a no-op).
    /// </summary>
    private Result<bool> FlipState(Deal deal, Guid productId, Guid? reviewedByUserId, bool supersede)
    {
        if (supersede)
        {
            var corrected = deal.Correct(productId, reviewedByUserId ?? Guid.Empty, clock);
            return corrected.IsSuccess ? true : corrected.Error;
        }

        switch (deal.Status)
        {
            case DealStatus.Rejected:
                return Deal.AlreadyRejected;
            case DealStatus.Confirmed:
                return false; // re-drive of an already-confirmed deal — no state change, resume side effects
            default:
                var flip = reviewedByUserId is { } by
                    ? deal.Confirm(productId, by, clock)
                    : deal.AutoConfirm(productId, clock);
                return flip.IsSuccess ? true : flip.Error;
        }
    }

    private async Task UpsertMemoryAsync(Deal deal, Guid productId, Guid? by, CancellationToken ct)
    {
        var existing = await memories.FindByKeyAsync(deal.StoreId, deal.NormalizedName, ct);
        if (existing is null)
        {
            // The deal was normalized at stage time; stamp the memory with the running normalizer version
            // (the Deal persists only the normalized string, not the version — the common no-bump case).
            var normalized = new NormalizedName(deal.NormalizedName, DealNormalizer.NormalizerVersion);
            var memory = DealMatchMemory.Remember(
                deal.HouseholdId, deal.StoreId, normalized, deal.RawName, productId, by, clock);
            await memories.AddAsync(memory, ct);
        }
        else
        {
            existing.Repoint(productId, by, clock);
        }

        await memories.SaveChangesAsync(ct);
    }
}
