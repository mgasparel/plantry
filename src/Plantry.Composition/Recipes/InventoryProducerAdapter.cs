using Microsoft.Extensions.Logging;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Application;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Recipes;

/// <summary>
/// Composition-root adapter for <see cref="IInventoryProducer"/> (yield-on-cook, plantry-854a) — the ADD
/// counterpart to <see cref="InventoryConsumerAdapter"/>. Delegates to Inventory's intake primitive
/// (<see cref="AddStockCommand"/> / <see cref="ProductStock.AddStock"/>) with
/// <see cref="StockSourceType.Cook"/>, the cook event id as the source reference, and the produce line id
/// as the idempotency <c>sourceLineRef</c> — so a re-driven produce (reconciliation) never double-adds the
/// yield lot, and every produced lot is traceable to its originating <c>CookEvent</c> (ADR-011). Lives in
/// the composition root, which references both contexts; the Recipes projects stay <c>→ SharedKernel only</c>.
///
/// <para>Recipes has no location concept of its own, so the produced lot's storage location is resolved
/// through a three-rung fallback chain (plantry-iypo): (1) the yielded product's own configured default
/// location (<c>Product.DefaultLocationId</c>) — the same "where this normally lives" field Take Stock
/// and product intake already use; (2) the household's configured default storage location
/// (<c>HouseholdInventorySettings.DefaultLocationId</c>, via <see cref="IHouseholdDefaultLocationReader"/>)
/// when the product has none; (3) the household's first active Location (alphabetical,
/// <see cref="ILocationRepository.ListActiveAsync"/>) when neither default is set. Either default is
/// skipped, with a warning logged, if it points at a location that has since been archived — a default
/// is only ever resolved among the currently active locations. Auto-created yield products carry no
/// product-level default, so rungs (2)/(3) are the deterministic storage target for them; when the
/// household has no active location at all the produce cannot be recorded and throws (the cook flow
/// records the line Failed).</para>
/// </summary>
public sealed class InventoryProducerAdapter(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    ILocationRepository locations,
    IHouseholdDefaultLocationReader householdDefaultLocation,
    IClock clock,
    ITenantContext tenant,
    ILogger<AddStockCommand> addLogger,
    ILogger<InventoryProducerAdapter> logger) : IInventoryProducer
{
    public async Task ProduceAsync(
        Guid productId,
        decimal quantity,
        Guid unitId,
        DateOnly? expiryDate,
        ProduceReason reason,
        Guid cookEventId,
        Guid userId,
        Guid sourceLineRef,
        CancellationToken ct = default)
    {
        // Map the narrow produce reason to Inventory's addition reason (plantry-a45c). StockReason.Cook
        // distinguishes a yield-on-cook add from an actual purchase in stock history; the
        // StockSourceType.Cook stamp (below) separately carries the machine-readable provenance
        // (cook event id) for waste/provenance analysis.
        var stockReason = reason switch
        {
            ProduceReason.Recipe => StockReason.Cook,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown ProduceReason."),
        };

        var activeLocations = await locations.ListActiveAsync(ct);
        if (activeLocations.Count == 0)
            throw new InvalidOperationException(
                "Cannot store cooked yield — the household has no active storage location.");

        // Three-rung fallback chain (plantry-iypo): product default → household default → alphabetically-
        // first active location. Each default is honored only when it still resolves to an active
        // location; a default pointing at a since-archived location is skipped (with a warning) rather
        // than silently used, since AddStockCommand would otherwise store stock somewhere no longer meant
        // to receive it.
        var productInfo = await catalog.FindProductAsync(productId, ct);
        var productDefaultLocationId = productInfo?.DefaultLocationId;
        Guid locationId;
        if (productDefaultLocationId is { } productDefault && activeLocations.Any(l => l.Id.Value == productDefault))
        {
            locationId = productDefault;
        }
        else
        {
            if (productDefaultLocationId is { } staleProductDefault)
                logger.LogWarning(
                    "Produce {ProductId}: configured product default location {LocationId} is not active; " +
                    "checking the household default next.", productId, staleProductDefault);

            var householdDefaultLocationId = await householdDefaultLocation.GetDefaultLocationIdAsync(ct);
            if (householdDefaultLocationId is { } householdDefault && activeLocations.Any(l => l.Id.Value == householdDefault))
            {
                locationId = householdDefault;
            }
            else
            {
                if (householdDefaultLocationId is { } staleHouseholdDefault)
                    logger.LogWarning(
                        "Produce {ProductId}: configured household default location {LocationId} is not " +
                        "active; falling back to the first active location {FallbackLocationId}.",
                        productId, staleHouseholdDefault, activeLocations[0].Id.Value);
                locationId = activeLocations[0].Id.Value;
            }
        }

        var command = new AddStockCommand(
            productId,
            quantity,
            unitId,
            locationId,
            userId,
            skuId: null,
            expiryDate: expiryDate,
            purchasedAt: null,
            stocks,
            catalog,
            clock,
            tenant,
            sourceType: StockSourceType.Cook,
            logger: addLogger,
            sourceRef: cookEventId,
            sourceLineRef: sourceLineRef,
            reason: stockReason);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Inventory produce failed ({result.Error.Code}): {result.Error.Description}");
    }
}
