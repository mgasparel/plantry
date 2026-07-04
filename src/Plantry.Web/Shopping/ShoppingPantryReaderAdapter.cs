using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-layer adapter implementing <see cref="IShoppingPantryReader"/> over the Inventory
/// bounded context's persistence and catalog read facade. This is the anti-corruption layer
/// seam between Shopping and Inventory — Shopping never takes a direct dependency on Inventory's
/// EF context or repositories (ADR-002). Follows the same adapter pattern as
/// <c>InventoryStockReaderAdapter</c> (Recipes → Inventory ACL).
///
/// <para>The adapter calls <see cref="IProductStockRepository.ListForHouseholdAsync"/> once
/// (the same call <c>InventoryQueryService</c> makes for the pantry list) and derives on-hand
/// quantities using the same display-unit aggregation logic. This is intentional sharing of
/// the same underlying query path rather than adding a new repository method.</para>
/// </summary>
public sealed class ShoppingPantryReaderAdapter(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
    ITenantContext tenant)
    : IShoppingPantryReader
{
    public async Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, ShoppingPantryStockLevel>();

        if (tenant.HouseholdId is not { } householdGuid)
            return new Dictionary<Guid, ShoppingPantryStockLevel>();

        var householdId = HouseholdId.From(householdGuid);
        var wanted = new HashSet<Guid>(productIds);

        // Load all household stock aggregates; the RLS interceptor enforces household scoping
        // at the DB level (ADR-008 defense-in-depth — both EF filter and RLS must agree).
        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);
        var relevantStock = allStock.Where(s => wanted.Contains(s.ProductId)).ToList();

        if (relevantStock.Count == 0)
            return new Dictionary<Guid, ShoppingPantryStockLevel>();

        var levels = await AggregateStockLevelsAsync(relevantStock, ct);
        return levels.ToDictionary(l => l.ProductId);
    }

    /// <inheritdoc cref="IShoppingPantryReader.GetLowStockProductsAsync"/>
    public async Task<IReadOnlyList<ShoppingPantryStockLevel>> GetLowStockProductsAsync(
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return [];

        var householdId = HouseholdId.From(householdGuid);

        // Load all household stock aggregates; RLS scoping ensures household isolation.
        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);
        if (allStock.Count == 0)
            return [];

        var levels = await AggregateStockLevelsAsync(allStock, ct);
        // Restock candidates = running-low ∪ out. IsLow now means running-low only (false when out),
        // so out products (OnHand ≤ 0) must be re-included explicitly — a fully-depleted staple is
        // just as much a restock candidate as one that is merely low.
        return levels.Where(l => l.IsLow || l.OnHand <= 0m).ToList();
    }

    // ── Shared aggregation helper ─────────────────────────────────────────────

    /// <summary>
    /// Aggregates on-hand quantities for the given product-stock records into
    /// <see cref="ShoppingPantryStockLevel"/> instances. Loads catalog product info and
    /// unit converters in batch calls; skips products whose catalog entry is missing.
    /// </summary>
    private async Task<List<ShoppingPantryStockLevel>> AggregateStockLevelsAsync(
        List<ProductStock> stockRecords,
        CancellationToken ct)
    {
        // Load catalog info for default unit id and unit code for relevant products.
        var allCatalogProducts = await catalog.ListProductsAsync(ct);
        var catalogByProduct = allCatalogProducts.ToDictionary(p => p.Id);

        // Load converters for all relevant products in one batch call.
        var stockProductIds = stockRecords.Select(s => s.ProductId).ToList();
        var convertersByProduct = await conversions.ForProductsAsync(stockProductIds, ct);

        var result = new List<ShoppingPantryStockLevel>(stockRecords.Count);

        foreach (var productStock in stockRecords)
        {
            if (!catalogByProduct.TryGetValue(productStock.ProductId, out var catalogInfo))
                continue; // product no longer in catalog — skip

            var activeLots = productStock.ActiveLotsFefo().ToList();

            var defaultUnitId = catalogInfo.DefaultUnitId;
            var unitCode = catalogInfo.DefaultUnitCode;
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

            // IsLow means "running low" only: a positive but low quantity, 0 < onHand ≤ threshold
            // (per ProductStock.IsRunningLow). It is deliberately false when out (onHand ≤ 0) so the
            // Shopping subline renders out and low as distinct, mutually-exclusive states and never
            // shows "out · low" together. Out is surfaced separately via OnHand ≤ 0. The extra
            // total > 0m guard excludes the out-with-threshold case, which IsRunningLow alone treats
            // as low (onHand ≤ threshold is satisfied by onHand = 0).
            var isLow = total > 0m && productStock.IsRunningLow(total);

            result.Add(new ShoppingPantryStockLevel(
                ProductId: productStock.ProductId,
                OnHand: total,
                UnitCode: unitCode,
                IsLow: isLow));
        }

        return result;
    }
}
