using Microsoft.Extensions.Logging;
using Plantry.Intake.Application;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="IAmendPricePort"/> — supersedes a purchase's price observation with a
/// corrected quantity over the Pricing <see cref="RecordAmendedObservationCommand"/> (ADR-023 A7/A8).
///
/// <para><b>Owns the retry-idempotency <see cref="RecordAmendedObservationCommand"/> deliberately does not
/// (ADR-023 A10, spec acceptance #7).</b> <c>ImportLine.PriceObservationId</c> is Intake's own bookkeeping
/// copy of "the observation this line produced" and can go stale across contexts (ADR-014 — no shared
/// transaction): a prior attempt may have already superseded it in Pricing before a later step in that same
/// attempt failed to save on the Intake side, so a retry can be handed an id that is no longer the live row.
/// <see cref="IPriceObservationRepository.FindAsync"/> is documented to return a row regardless of its
/// superseded state for exactly this reason — this adapter walks <see cref="PriceObservation.SupersededById"/>
/// forward to the true live tail before deciding what to do:</para>
/// <list type="number">
/// <item>the live row's quantity already equals <paramref name="correctedQuantity"/> passed to
/// <see cref="AmendAsync"/> — the prior attempt's price leg already landed; skip re-superseding (a no-op
/// success returning the live row's id) rather than let <see cref="PriceObservation.Supersede"/> throw on
/// an already-bound row;</item>
/// <item>otherwise, delegate to <see cref="RecordAmendedObservationCommand"/> with the LIVE tail as the
/// original to supersede — this is what makes a second, genuinely new amendment (spec §3's 3 lb → 2.5 lb)
/// chain off the first amendment's row instead of re-targeting the original Purchase-time observation.</item>
/// </list>
/// </summary>
public sealed class AmendPriceAdapter(
    IPriceObservationRepository repository,
    IUnitPriceCalculator calculator,
    ITenantContext tenant,
    ILogger<RecordAmendedObservationCommand> logger) : IAmendPricePort
{
    public async Task<Result<Guid>> AmendAsync(
        Guid originalObservationId, decimal correctedQuantity, Guid userId, CancellationToken ct = default)
    {
        var live = await repository.FindAsync(PriceObservationId.From(originalObservationId), ct);
        if (live is null)
            return RecordAmendedObservationCommand.OriginalNotFound;

        // Walk forward to the true live (not-yet-superseded) tail — the id we were handed may be stale.
        while (live.SupersededById is { } nextId)
        {
            var next = await repository.FindAsync(nextId, ct);
            if (next is null)
                return RecordAmendedObservationCommand.OriginalNotFound;
            live = next;
        }

        // Idempotent re-drive (A10): a prior attempt's price leg already landed at this corrected quantity.
        if (live.Quantity == correctedQuantity)
            return live.Id.Value;

        var command = new RecordAmendedObservationCommand(
            live.Id, correctedQuantity, userId, repository, calculator, tenant, logger);

        var result = await command.ExecuteAsync(ct);
        return result.IsFailure ? result.Error : result.Value.Value;
    }
}
