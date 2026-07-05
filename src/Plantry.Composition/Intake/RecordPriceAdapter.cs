using Plantry.Intake.Application;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="IRecordPricePort"/> — writes a purchase-sourced price observation
/// during intake commit over the Pricing <see cref="RecordObservationCommand"/>. The unit price soft-fails
/// to null on cross-dimension (DM-17); the observation is still recorded. Throws only on a hard command
/// failure so the per-line commit can abort that line cleanly.
/// </summary>
public sealed class RecordPriceAdapter(
    IPriceObservationRepository repository,
    IUnitPriceCalculator calculator,
    ITenantContext tenant) : IRecordPricePort
{
    public async Task<Guid> RecordAsync(
        Guid productId, Guid? skuId, decimal price, decimal quantity, Guid unitId,
        string? merchantText, Guid? storeId, Guid sourceRef, DateTimeOffset observedAt, Guid userId, CancellationToken ct = default)
    {
        var command = new RecordObservationCommand(
            productId, skuId, price, quantity, unitId, merchantText, sourceRef, observedAt, userId,
            PriceSource.Purchase, repository, calculator, tenant, storeId: storeId);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Record price failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }
}
