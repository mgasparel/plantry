using Microsoft.Extensions.Logging;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Pricing.Application;

/// <summary>
/// Amendment leg of ADR-023 (Pricing mechanics, A7/A8): re-derives the price observation for a purchase
/// whose committed quantity was corrected after intake commit. Mirrors <see cref="RecordObservationCommand"/>'s
/// shape — same repository/calculator/tenant seam, same <see cref="Result{T}"/> return — but instead of
/// writing a fresh observation from scratch, it produces the <b>amending</b> row via
/// <see cref="PriceObservation.RecordAmendment"/> (same <c>Price</c>/<c>ObservedAt</c>/<c>SourceRef</c> as
/// the original, corrected quantity, re-derived unit price) and binds the original's
/// <see cref="PriceObservation.SupersededById"/> via <see cref="PriceObservation.Supersede"/> in the same
/// unit of work.
/// <para>
/// The caller (a future Intake <c>AmendCommittedLineCommand</c>, plantry-hitc) is responsible for deciding
/// <i>whether</i> this should run at all — A8: an each-count amendment of a weight-priced line leaves the
/// weight-denominated observation untouched, so the caller re-derives first and only calls this command
/// when the corrected quantity actually feeds the observation. This command always produces + supersedes;
/// it does not itself decide eligibility.
/// </para>
/// <para>
/// Repeats chain off the live row for free: <paramref name="originalObservationId"/> must be the
/// <b>current</b> (not-yet-superseded) observation — <see cref="PriceObservation.Supersede"/> throws if it
/// is already bound, which is exactly the "never fork off an already-superseded row" guard from A7.
/// </para>
/// </summary>
public sealed class RecordAmendedObservationCommand(
    PriceObservationId originalObservationId,
    decimal correctedQuantity,
    Guid userId,
    IPriceObservationRepository repository,
    IUnitPriceCalculator calculator,
    ITenantContext tenant,
    ILogger<RecordAmendedObservationCommand> logger)
{
    public static readonly Error OriginalNotFound = Error.Custom(
        "Pricing.RecordAmendedObservation.OriginalNotFound",
        "The observation being amended does not exist in this household.");

    public async Task<Result<PriceObservationId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var original = await repository.FindAsync(originalObservationId, ct);
        if (original is null)
        {
            logger.LogWarning(
                "RecordAmendedObservation: original observation {ObservationId} not found in this household.",
                originalObservationId.Value);
            return OriginalNotFound;
        }

        // A8: re-run the commit-time unit-price derivation with the corrected quantity — never a naive
        // scale of the old unit price (a weight→each cross-dimension line soft-fails to null exactly as
        // it would at commit time).
        var unitPrice = await calculator.TryNormalizeAsync(original.Price, correctedQuantity, original.UnitId, ct);

        var amendment = PriceObservation.RecordAmendment(original, correctedQuantity, unitPrice, userId);

        // Throws if `original` is already superseded — repeats must chain off the live row (A7).
        original.Supersede(amendment.Id);

        await repository.AddAsync(amendment, ct);
        await repository.SaveChangesAsync(ct);

        logger.LogInformation(
            "RecordAmendedObservation: product {ProductId} — {OriginalId} superseded by {AmendmentId} " +
            "(corrected quantity {CorrectedQuantity}).",
            original.ProductId, originalObservationId.Value, amendment.Id.Value, correctedQuantity);

        return amendment.Id;
    }
}
