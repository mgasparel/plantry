using Microsoft.Extensions.Logging;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Pricing.Application;

/// <summary>
/// Records a price observation, normalizing to a per-base-unit price via
/// <see cref="IUnitPriceCalculator"/>. A null unit price is stored as-is (soft-fail).
/// </summary>
public sealed class RecordObservationCommand(
    Guid productId,
    Guid? skuId,
    decimal price,
    decimal quantity,
    Guid unitId,
    string? merchantText,
    Guid? sourceRef,
    DateTimeOffset observedAt,
    Guid userId,
    PriceSource source,
    IPriceObservationRepository repository,
    IUnitPriceCalculator calculator,
    ITenantContext tenant,
    ILogger<RecordObservationCommand> logger,
    DateOnly? validFrom = null,
    DateOnly? validTo = null,
    Guid? storeId = null)
{
    public async Task<Result<PriceObservationId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
        {
            logger.LogWarning(
                "RecordObservation: no household in tenant context — rejecting {Source} observation for product {ProductId}.",
                source, productId);
            return Error.Unauthorized;
        }

        var unitPrice = await calculator.TryNormalizeAsync(price, quantity, unitId, ct);

        var observation = PriceObservation.Record(
            HouseholdId.From(householdId),
            productId,
            skuId,
            price,
            quantity,
            unitId,
            unitPrice,
            source,
            merchantText,
            sourceRef,
            observedAt,
            userId,
            validFrom,
            validTo,
            storeId);

        await repository.AddAsync(observation, ct);
        await repository.SaveChangesAsync(ct);

        logger.LogInformation(
            "RecordObservation: product {ProductId} — {Source} observation {ObservationId} recorded " +
            "(unit price normalization soft-failed: {UnitPriceSoftFailed}).",
            productId, source, observation.Id.Value, unitPrice is null);

        return observation.Id;
    }
}
