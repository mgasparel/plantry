using Plantry.SharedKernel;

namespace Plantry.Intake.Application;

/// <summary>
/// Port: cross-context call to Inventory to amend an already-committed purchase's quantity (ADR-023 A10).
/// Implemented in Plantry.Web (adapter over Inventory's <c>AmendPurchaseCommand</c>), mirroring
/// <see cref="IAddStockPort"/>'s shape for the commit-time write.
///
/// <para>Unlike the commit-time ports, which throw on failure because a failure there is an unexpected
/// abort, Inventory's amendment guards (below-consumed, closed-by-correction, depleted lot, …) are
/// <b>expected, user-facing outcomes</b> — core to the feature, not bugs — so this port returns a
/// <see cref="Result{T}"/> instead, letting <c>AmendCommittedLineCommand</c> propagate the exact domain
/// error code back to the caller (spec acceptance #3/#4).</para>
/// </summary>
public interface IAmendStockPort
{
    /// <summary>
    /// Amends the lot's purchased quantity to <paramref name="correctedQuantity"/> (ADR-023 A2/A3),
    /// returning the signed delta applied to the lot (zero for an idempotent re-drive, A10), or the
    /// rejecting domain error (e.g. <c>Inventory.AmendBelowConsumed</c>, <c>Inventory.AmendmentClosedByCorrection</c>).
    /// </summary>
    Task<Result<decimal>> AmendAsync(
        Guid productId,
        Guid stockEntryId,
        decimal correctedQuantity,
        Guid importLineId,
        Guid userId,
        CancellationToken ct = default);
}
