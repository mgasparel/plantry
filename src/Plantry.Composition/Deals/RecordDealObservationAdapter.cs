using Plantry.Deals.Application;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Deals;

/// <summary>
/// Web-side adapter for <see cref="IPriceObservationWriter"/> — writes a <b>deal-sourced</b> price
/// observation when a deal is confirmed/corrected, over Pricing's <see cref="RecordObservationCommand"/>
/// (extended by P5-P with the validity window + store id). Deals holds only the soft-refs; this composition
/// root wraps the Pricing command so <c>Plantry.Deals</c> never touches <c>PricingDbContext</c>
/// (ADR-010/DM-3) — the deal twin of intake's <c>RecordPriceAdapter</c>.
///
/// <para>A deal may not advertise a pack size, so <c>quantity</c>/<c>unitId</c> can be null; they map to a
/// quantity of 1 and an empty unit, and the unit price soft-fails to null (DM-17) while the observation is
/// still recorded. A missing reviewer (memory auto-confirm) maps to <see cref="Guid.Empty"/>. Throws only
/// on a hard command failure so the per-deal commit can abort that deal cleanly.</para>
/// </summary>
public sealed class RecordDealObservationAdapter(
    IPriceObservationRepository repository,
    IUnitPriceCalculator calculator,
    ITenantContext tenant) : IPriceObservationWriter
{
    public async Task<Guid> RecordObservationAsync(
        Guid productId,
        decimal price,
        decimal? quantity,
        Guid? unitId,
        Guid storeId,
        DateOnly validFrom,
        DateOnly validTo,
        Guid dealId,
        Guid? reviewedByUserId,
        DateTimeOffset observedAt,
        CancellationToken ct = default)
    {
        var command = new RecordObservationCommand(
            productId,
            skuId: null,
            price,
            quantity ?? 1m,
            unitId ?? Guid.Empty,
            merchantText: null, // deals carry a resolved store_id, not free-text merchant provenance
            dealId,
            observedAt,
            reviewedByUserId ?? Guid.Empty,
            PriceSource.Deal,
            repository,
            calculator,
            tenant,
            validFrom: validFrom,
            validTo: validTo,
            storeId: storeId);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Record deal observation failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }
}
