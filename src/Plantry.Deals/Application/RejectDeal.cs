using Microsoft.Extensions.Logging;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Deals.Application;

/// <summary>
/// DJ4 reject seam (deals-domain-model §7). Flips a <see cref="Deal"/> to
/// <see cref="DealStatus.Rejected"/> and, optionally, records a <b>negative</b>
/// <see cref="DealMatchMemory"/> (DL-O3) so the next pull auto-rejects the same
/// <c>(store, normalized_name)</c> rather than re-queuing it. Writes <b>no</b> price observation (D5) —
/// a rejected deal never reaches Pricing. The state flip and the optional memory write are separate
/// transactions, mirroring <see cref="ConfirmDeal"/>.
/// </summary>
public sealed class RejectDeal(
    IDealRepository deals,
    IDealMatchMemoryRepository memories,
    IClock clock,
    ITenantContext tenant,
    ILogger<RejectDeal> logger)
{
    public static readonly Error CommitFailed = Error.Custom(
        "Deals.RejectDeal.CommitFailed",
        "The negative-memory side effect failed mid-reject; the reject is resumable on retry.");

    /// <summary>
    /// Rejects the deal (idempotent). When <paramref name="rememberNegative"/> is true, upserts a negative
    /// memory for the deal's key so the pattern is not re-surfaced next pull.
    /// </summary>
    public async Task<Result> RejectAsync(
        DealId dealId, Guid reviewedByUserId, bool rememberNegative = false, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var deal = await deals.FindAsync(dealId, ct);
        if (deal is null)
            return Error.NotFound;

        try
        {
            // ── Transaction A: flip to Rejected (idempotent; clears ProductId, writes no observation). ──
            var reject = deal.Reject(reviewedByUserId, clock);
            if (reject.IsFailure)
                return reject.Error;
            await deals.SaveChangesAsync(ct); // commits state (+ raises DealRejected on the actual transition)

            // ── Transaction B (optional): remember a negative match so the next pull auto-rejects (DL-O3). ──
            if (rememberNegative)
                await RememberNegativeAsync(deal, reviewedByUserId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reject deal {DealId} failed mid-commit; retry is resumable.", dealId.Value);
            return CommitFailed;
        }

        logger.LogInformation(
            "Deal {DealId} rejected (rememberNegative={RememberNegative}).", dealId.Value, rememberNegative);
        return Result.Success();
    }

    private async Task RememberNegativeAsync(Deal deal, Guid by, CancellationToken ct)
    {
        var existing = await memories.FindByKeyAsync(deal.StoreId, deal.NormalizedName, ct);
        if (existing is null)
        {
            var normalized = new NormalizedName(deal.NormalizedName, DealNormalizer.NormalizerVersion);
            var memory = DealMatchMemory.RememberNegative(
                deal.HouseholdId, deal.StoreId, normalized, deal.RawName, by, clock);
            await memories.AddAsync(memory, ct);
        }
        else
        {
            existing.Forget(clock); // an existing positive memory becomes negative ("not a tracked product")
        }

        await memories.SaveChangesAsync(ct);
    }
}
