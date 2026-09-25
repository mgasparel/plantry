using Microsoft.Extensions.Logging;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Market.Application;

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
    IPriceObservationRepository priceObservations,
    IUnitPriceCalculator unitPriceCalculator,
    IClock clock,
    ITenantContext tenant,
    ILogger<ConfirmDeal> logger,
    ILogger<RecordObservationCommand> priceLogger)
{
    public static readonly Error UnknownProduct = Error.Custom(
        "Deals.ConfirmDeal.UnknownProduct",
        "The resolved product does not exist in this household's catalog.");

    public static readonly Error CommitFailed = Error.Custom(
        "Deals.ConfirmDeal.CommitFailed",
        "A cross-context side effect failed mid-confirm; the confirm is resumable on retry.");

    /// <summary>plantry-oh27.6: a deal resolved to a parent product with zero live variants can never hit
    /// at intake (there is nothing to fan the observation out to) — confirm is rejected before any write.</summary>
    public static readonly Error ParentHasNoVariants = Error.Custom(
        "Deals.ParentHasNoVariants",
        "This product has no variants to apply the deal to.");

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

        // plantry-oh27.6: resolve IsParent/LiveVariantIds so a parent match fans out at write time. A miss
        // here (a test double or a future adapter that doesn't yet populate ForProductsAsync) degrades to a
        // concrete leaf — the pre-oh27.6 single-observation behaviour — never a hard failure.
        var productInfoMap = await products.ForProductsAsync([productId], ct);
        var productInfo = productInfoMap.TryGetValue(productId, out var info)
            ? info
            : new DealProductInfo(productId, string.Empty, null);

        if (productInfo.IsParent && productInfo.LiveVariantIds.Count == 0)
        {
            logger.LogWarning(
                "Confirm deal {DealId} rejected — resolved parent product {ProductId} has no live variants.",
                dealId.Value, productId);
            return ParentHasNoVariants;
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
                var observationId = await RecordDealObservationsAsync(
                    resolvedProduct, productInfo, deal, reviewedByUserId, supersede, ct);

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

    /// <summary>
    /// Writes the deal-sourced price observation(s) for a confirm/correct (plantry-oh27.6). A concrete
    /// (leaf) resolution writes exactly one row — the pre-oh27.6 behaviour. A <b>parent</b> resolution fans
    /// the same price/quantity/unit/window/store out to <b>every live variant</b>, one
    /// <see cref="RecordObservationCommand"/> per variant, each stamped with the deal's id as
    /// <c>SourceRef</c> — so Intake's leaf-keyed <see cref="DealHitMatcher"/> fires unchanged when any
    /// variant is purchased at the deal price.
    ///
    /// <para><b>Resumability (DD2).</b> <paramref name="supersede"/> false (Confirm/AutoConfirm): a variant
    /// that already has a live Deal observation with this deal's <c>SourceRef</c> is skipped — a re-drive
    /// after a partial fan-out (crash mid-loop) writes only the variants still missing, never a duplicate.
    /// <paramref name="supersede"/> true (Correct): <b>every</b> live Deal observation with this deal's
    /// <c>SourceRef</c> is superseded (<see cref="PriceObservation.Supersede"/>) and a fresh row is written
    /// for every current target variant, regardless of what existed before — the prior rows stay as history
    /// (DM-17/R1).</para>
    ///
    /// <para><see cref="Deal.CommittedPriceObservationId"/> stays a single id (resumability, DD2): it links
    /// the lowest-ordered target variant's observation (new or reused), deterministic across re-drives — it
    /// is provenance for "the fan-out landed", never "the only observation" (see
    /// <see cref="Deal.LinkObservation"/>'s doc comment).</para>
    /// </summary>
    private async Task<Guid> RecordDealObservationsAsync(
        Guid resolvedProduct, DealProductInfo productInfo, Deal deal, Guid? reviewedByUserId, bool supersede,
        CancellationToken ct)
    {
        var targetVariantIds = productInfo.IsParent
            ? productInfo.LiveVariantIds.OrderBy(id => id).ToList()
            : [resolvedProduct];

        var existingLive = await priceObservations.ListLiveBySourceRefAsync(PriceSource.Deal, deal.Id.Value, ct);
        var existingByProduct = existingLive
            .GroupBy(o => o.ProductId)
            .ToDictionary(g => g.Key, g => g.First());

        var newByProduct = new Dictionary<Guid, Guid>();
        foreach (var variantId in targetVariantIds)
        {
            // Resumable skip: a Confirm/AutoConfirm re-drive never rewrites a variant that already landed.
            // A Correct always rewrites every target variant fresh — its old rows are superseded below.
            if (!supersede && existingByProduct.ContainsKey(variantId))
                continue;

            newByProduct[variantId] = await WriteOneObservationAsync(variantId, deal, reviewedByUserId, ct);
        }

        // Every target variant now resolves to an observation id — freshly written, or (Confirm/AutoConfirm
        // only) reused from a prior partial fan-out.
        var combined = new Dictionary<Guid, Guid>();
        foreach (var variantId in targetVariantIds)
        {
            if (newByProduct.TryGetValue(variantId, out var freshId))
                combined[variantId] = freshId;
            else if (existingByProduct.TryGetValue(variantId, out var existing))
                combined[variantId] = existing.Id.Value;
        }

        if (supersede && existingLive.Count > 0)
        {
            // Fallback replacement for an old row whose product fell out of the new target set entirely
            // (e.g. Correct re-resolves to a different parent with no shared variants) — still needs SOME
            // replacement id to satisfy the one-time Supersede bind; the lowest-ordered fresh row is as
            // good as any, since this is audit provenance, not a per-product chain.
            var fallbackReplacement = combined.OrderBy(kv => kv.Key).First().Value;
            foreach (var old in existingLive)
            {
                var replacement = combined.TryGetValue(old.ProductId, out var r) ? r : fallbackReplacement;
                old.Supersede(PriceObservationId.From(replacement));
            }
            await priceObservations.SaveChangesAsync(ct);
        }

        return combined.OrderBy(kv => kv.Key).First().Value;
    }

    /// <summary>
    /// Writes one <b>deal-sourced</b> price observation directly against <see cref="RecordObservationCommand"/>
    /// (formerly the Deals→Pricing ACL adapter <c>RecordDealObservationAdapter</c>, plantry-riqy — collapsed
    /// into an intra-context call now that both halves live in Plantry.Market, ADR-024).
    ///
    /// <para>A deal may not advertise a pack size, so <c>quantity</c>/<c>unitId</c> can be null; they map to a
    /// quantity of 1 and an empty unit, and the unit price soft-fails to null (DM-17) while the observation is
    /// still recorded. A missing reviewer (memory auto-confirm) maps to <see cref="Guid.Empty"/>. Throws only
    /// on a hard command failure so the per-deal commit can abort that deal cleanly.</para>
    /// </summary>
    private async Task<Guid> WriteOneObservationAsync(
        Guid productId, Deal deal, Guid? reviewedByUserId, CancellationToken ct)
    {
        var command = new RecordObservationCommand(
            productId,
            skuId: null,
            deal.Price,
            deal.Quantity ?? 1m,
            deal.UnitId ?? Guid.Empty,
            merchantText: null, // deals carry a resolved store_id, not free-text merchant provenance
            deal.Id.Value,
            clock.UtcNow,
            reviewedByUserId ?? Guid.Empty,
            PriceSource.Deal,
            priceObservations,
            unitPriceCalculator,
            tenant,
            priceLogger,
            validFrom: deal.ValidityWindow.ValidFrom,
            validTo: deal.ValidityWindow.ValidTo,
            storeId: deal.StoreId);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Record deal observation failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }
}
