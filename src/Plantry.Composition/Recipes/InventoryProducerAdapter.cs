using Microsoft.Extensions.Logging;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
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
/// <para>Recipes has no location concept, so the produced lot is stored in the household's first active
/// Location (there is no per-household default-location query). Auto-created yield products carry no
/// default location, so this deterministic fallback is the storage target; when the household has no active
/// location the produce cannot be recorded and throws (the cook flow records the line Failed).</para>
/// </summary>
public sealed class InventoryProducerAdapter(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant,
    ILogger<AddStockCommand> addLogger) : IInventoryProducer
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
        // Map the narrow produce reason to Inventory's addition reason. StockReason.Purchase is the only
        // non-Correction addition reason; the StockSourceType.Cook stamp distinguishes a yield-on-cook add
        // from an actual purchase for waste/provenance analysis.
        _ = reason switch
        {
            ProduceReason.Recipe => StockReason.Purchase,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown ProduceReason."),
        };

        var activeLocations = await locations.ListActiveAsync(ct);
        if (activeLocations.Count == 0)
            throw new InvalidOperationException(
                "Cannot store cooked yield — the household has no active storage location.");
        var locationId = activeLocations[0].Id.Value;

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
            sourceLineRef: sourceLineRef);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Inventory produce failed ({result.Error.Code}): {result.Error.Description}");
    }
}
