using Plantry.SharedKernel;

namespace Plantry.Intake.Application;

/// <summary>
/// Port: cross-context call to Pricing to supersede a purchase's price observation with a corrected
/// quantity (ADR-023 A7/A8). Implemented in Plantry.Web (adapter over Pricing's
/// <c>RecordAmendedObservationCommand</c>), mirroring <see cref="IRecordPricePort"/>'s shape for the
/// commit-time write.
///
/// <para>The caller (<c>AmendCommittedLineCommand</c>) decides <i>whether</i> to call this at all — A8:
/// only when the corrected quantity actually feeds the observation
/// (<see cref="LineCommitDecision.DecidePriceAmendment"/>). Returns a <see cref="Result{T}"/>, not a
/// throw — a missing original observation is an expected re-drive edge case, not an abort.</para>
///
/// <para><b>The implementation, not the caller, owns A10 retry-idempotency for this leg.</b>
/// <paramref name="originalObservationId"/> may be stale (the caller's own bookkeeping copy of "the
/// observation this line produced," which can lag behind Pricing's actual state across the ADR-014
/// context boundary). An implementation must resolve to the TRUE live (not-yet-superseded) observation
/// before acting, and treat a live row that already matches <paramref name="correctedQuantity"/> as a
/// no-op success (returning the live row's id) rather than attempting — and failing — a second supersede
/// of an already-bound row.</para>
/// </summary>
public interface IAmendPricePort
{
    /// <summary>Resolves the live observation in <paramref name="originalObservationId"/>'s chain,
    /// re-derives it for <paramref name="correctedQuantity"/> and supersedes it (or no-ops if the live row
    /// already matches), returning the id of the live observation afterward — the new amending row, or the
    /// same row when no-op'd.</summary>
    Task<Result<Guid>> AmendAsync(
        Guid originalObservationId,
        decimal correctedQuantity,
        Guid userId,
        CancellationToken ct = default);
}
