using Microsoft.Extensions.Logging;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Market.Application;

/// <summary>
/// Records a price observation, normalizing to a per-base-unit price via
/// <see cref="IUnitPriceCalculator"/>. A null unit price is stored as-is (soft-fail).
/// <para>
/// Intake-time deal-hit detection (plantry-j9q4): a <see cref="PriceSource.Purchase"/> observation with a
/// resolved <see cref="storeId"/> and a normalizable unit price is checked via <see cref="DealHitMatcher"/>
/// against the cheapest Confirmed deal for the same product, at the same store, whose validity window
/// covers <paramref name="observedAt"/>. The match is stamped onto the new row's
/// <see cref="PriceObservation.MatchedDealId"/> at construction time (ADR-023: never a later mutating
/// update). See <see cref="DealHitMatcher"/> for the matching rules and cross-context rationale — the same
/// helper backs <see cref="RecordAmendedObservationCommand"/>'s re-evaluation of the match when a
/// quantity correction re-derives the unit price.
/// </para>
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
        var matchedDealId = await DealHitMatcher.FindAsync(repository, source, productId, storeId, unitPrice, observedAt, ct);

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
            storeId,
            matchedDealId);

        await repository.AddAsync(observation, ct);
        await repository.SaveChangesAsync(ct);

        logger.LogInformation(
            "RecordObservation: product {ProductId} — {Source} observation {ObservationId} recorded " +
            "(unit price normalization soft-failed: {UnitPriceSoftFailed}, matched deal: {MatchedDealId}).",
            productId, source, observation.Id.Value, unitPrice is null, matchedDealId);

        return observation.Id;
    }
}
