using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using RecipesProductStock = Plantry.Recipes.Application.ProductStock;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IInventoryStockReader"/> — supplies <c>FulfillmentService</c>
/// with live stock snapshots (available quantity + soonest expiry) by calling directly into
/// Inventory's persistence layer (<see cref="IProductStockRepository"/>) and Catalog's conversion
/// layer (<see cref="IProductConversionProvider"/>). Lives in Plantry.Web, the composition root
/// that already references both contexts, so the Recipes projects stay <c>→ SharedKernel only</c>.
///
/// The available quantity in the returned <see cref="RecipesProductStock"/> is aggregated into the
/// product's default unit from all active FEFO-ordered lots — the same logic
/// <see cref="InventoryQueryService"/> uses for the pantry list. For parent products (DM-19),
/// <c>FulfillmentService</c> requests each variant child's id individually through this adapter,
/// so a single-product lookup is the hot path.
/// </summary>
public sealed class InventoryStockReaderAdapter(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant)
    : IInventoryStockReader
{
    public async Task<RecipesProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
    {
        var batch = await FindStockBatchAsync([productId], ct);
        return batch.GetValueOrDefault(productId);
    }

    public async Task<IReadOnlyDictionary<Guid, RecipesProductStock>> FindStockBatchAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, RecipesProductStock>();

        if (tenant.HouseholdId is not { } householdGuid)
            return new Dictionary<Guid, RecipesProductStock>();

        var householdId = HouseholdId.From(householdGuid);
        var distinctIds = productIds.Distinct().ToList();
        var wanted = new HashSet<Guid>(distinctIds);

        // Load stock aggregates with lots for all household products, then filter.
        // The RLS connection interceptor enforces household scoping at the DB level; the in-memory
        // HouseholdId parameter mirrors the EF query filter — both must agree (ADR-008).
        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);
        var relevantStock = allStock.Where(s => wanted.Contains(s.ProductId)).ToList();

        if (relevantStock.Count == 0)
            return new Dictionary<Guid, RecipesProductStock>();

        // Load converters for all relevant products in a single batch call.
        var stockProductIds = relevantStock.Select(s => s.ProductId).ToList();
        var convertersByProduct = await conversions.ForProductsAsync(stockProductIds, ct);

        // Load catalog info (for default unit id) for all relevant products.
        // GetUnitCodesAsync returns all units — used below to map unit ids to codes if needed.
        // FindProductAsync is called per-product here; a batch FindManyAsync would be cleaner but
        // is not yet in the repository interface — tracked as a follow-up.
        var catalogByProduct = new Dictionary<Guid, Plantry.Inventory.Application.CatalogProductInfo>();
        foreach (var productId in stockProductIds)
        {
            var info = await catalog.FindProductAsync(productId, ct);
            if (info is not null)
                catalogByProduct[productId] = info;
        }

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var result = new Dictionary<Guid, RecipesProductStock>(distinctIds.Count);

        foreach (var productStock in relevantStock)
        {
            var activeLots = productStock.ActiveLotsFefo().ToList();
            if (activeLots.Count == 0)
                continue; // all lots depleted — no available stock

            if (!catalogByProduct.TryGetValue(productStock.ProductId, out var catalogInfo))
                continue; // product no longer in catalog — skip

            var defaultUnitId = catalogInfo.DefaultUnitId;
            var converter = convertersByProduct.TryGetValue(productStock.ProductId, out var c)
                ? c
                : await conversions.ForProductAsync(productStock.ProductId, ct);

            // Aggregate quantity into the product's default unit (mirrors InventoryQueryService logic).
            var total = 0m;
            foreach (var lot in activeLots)
            {
                var converted = converter.Convert(lot.Quantity, lot.UnitId, defaultUnitId);
                if (converted.IsSuccess)
                    total += converted.Value;
                // On conversion failure the lot contributes 0; honest degradation preferred over crash.
            }

            // Soonest expiry across active lots (null when no lot carries an expiry date).
            DateOnly? soonestExpiry = activeLots
                .Where(l => l.ExpiryDate.HasValue)
                .Select(l => l.ExpiryDate!.Value)
                .Cast<DateOnly?>()
                .DefaultIfEmpty(null)
                .Min();

            result[productStock.ProductId] = new RecipesProductStock(
                productStock.ProductId,
                total,
                defaultUnitId,
                soonestExpiry);
        }

        return result;
    }
}
