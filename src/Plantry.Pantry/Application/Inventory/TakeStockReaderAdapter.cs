using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Pantry.Application;

/// <summary>
/// Adapter for <see cref="ITakeStockReader"/> (P4-3 / TS-10). Composes:
/// <list type="bullet">
/// <item><see cref="IProductStockRepository"/> — active lot data from the Inventory side.</item>
/// <item><see cref="IProductRepository"/>, <see cref="IUnitRepository"/>,
/// <see cref="ILocationRepository"/> — Catalog reference data for names and default locations.</item>
/// <item><see cref="IProductConversionProvider"/> — unit conversion for display-unit aggregation.</item>
/// </list>
/// An intra-context Pantry collaboration now that Catalog and Inventory live in one assembly
/// (ADR-024, plantry-g3da.6) — same pattern as <see cref="CatalogReadFacade"/>. Since plantry-g3da.10
/// unified <c>CatalogDbContext</c>/<c>InventoryDbContext</c> into one <c>PantryDbContext</c>, every
/// repository above shares a single scoped DbContext instance — all queries here run strictly
/// sequentially (EF Core's single-threaded constraint), never "in parallel" via an unawaited Task.
/// </summary>
public sealed class TakeStockReaderAdapter(
    IProductStockRepository stocks,
    IProductRepository products,
    IUnitRepository units,
    ILocationRepository locations,
    IProductConversionProvider conversions,
    ICategoryRepository categories,
    ITenantContext tenant) : ITakeStockReader
{
    public async Task<IReadOnlyList<TakeStockLocationRow>> ListLocationsAsync(CancellationToken ct = default)
    {
        var activeLocations = await locations.ListActiveAsync(ct);
        return activeLocations
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => new TakeStockLocationRow(l.Id.Value, l.Name, l.LastCountedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<TakeStockLocationProductRow>> ListLocationRowsAsync(
        Guid locationId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return [];

        var household = HouseholdId.From(householdId);

        // All repositories share one scoped PantryDbContext (plantry-g3da.10) — every query below
        // must run sequentially, not concurrently (EF Core DbContext is single-threaded).
        var allStock = await stocks.ListForHouseholdAsync(household, ct);
        var allProducts = await products.ListActiveAsync(ct);
        var allUnits = await units.ListAsync(ct);
        var unitCodesById = allUnits.ToDictionary(u => u.Id.Value, u => u.Code);
        // Category lookup for the walk's category grouping (plantry-vvqt design item 6). ListAsync
        // (not ListActiveAsync) so an archived category referenced by an existing product still
        // resolves a name rather than the row falling into "Other" (categories are soft-deleted —
        // see Category.ArchivedAt — and products.category_id is a bare cross-row id with no FK).
        var allCategories = await categories.ListAsync(ct);
        var categoriesById = allCategories.ToDictionary(c => c.Id.Value);

        // Indexed for fast lookup.
        var stockByProductId = allStock.ToDictionary(s => s.ProductId);

        // Batch-load converters for all products that have active lots in this location.
        var productIdsWithActiveStockHere = allStock
            .Where(s => s.ActiveLotsFefo().Any(e => e.LocationId == locationId))
            .Select(s => s.ProductId)
            .Distinct();
        var convertersByProduct = await conversions.ForProductsAsync(productIdsWithActiveStockHere, ct);

        // Batch-load products with their conversions for SupportedUnits derivation (C10).
        // We need all product ids that will appear in the row set (both branch A and branch B).
        var branchAIds = allStock
            .Where(s => s.ActiveLotsFefo().Any(e => e.LocationId == locationId))
            .Where(s => allProducts.Any(p => p.Id.Value == s.ProductId && p.CanHoldStock))
            .Select(s => s.ProductId);
        var branchBIds = allProducts
            .Where(p => p.CanHoldStock && p.DefaultLocationId?.Value == locationId)
            .Select(p => p.Id.Value);
        var rowProductIds = branchAIds.Concat(branchBIds).Distinct();

        var productsWithConversions = await products.ListWithConversionsAsync(
            rowProductIds.Select(ProductId.From).ToList(), ct);
        var productConversionsById = productsWithConversions.ToDictionary(
            p => p.Id.Value,
            p => (IReadOnlyList<ProductConversion>)p.Conversions);

        var rows = new Dictionary<Guid, TakeStockLocationProductRow>();

        // Branch A: tracked products with active stock in this location.
        foreach (var stock in allStock)
        {
            var lotsHere = stock.ActiveLotsFefo()
                .Where(e => e.LocationId == locationId)
                .ToList();
            if (lotsHere.Count == 0) continue;

            var product = allProducts.SingleOrDefault(p => p.Id.Value == stock.ProductId);
            if (product is null || !product.CanHoldStock) continue;

            var converter = convertersByProduct.GetValueOrDefault(stock.ProductId)
                ?? new IdentityQuantityConverter();
            var displayUnitId = product.DefaultUnitId.Value;
            var displayUnitCode = unitCodesById.GetValueOrDefault(displayUnitId, "?");
            var total = SumInDisplayUnit(lotsHere, displayUnitId, converter);
            var productConversions = productConversionsById.GetValueOrDefault(stock.ProductId, []);
            var supportedUnits = BuildSupportedUnits(displayUnitId, allUnits, productConversions, unitCodesById);
            var (categoryName, categorySortOrder) = ResolveCategory(product.CategoryId, categoriesById);

            rows[stock.ProductId] = new TakeStockLocationProductRow(
                stock.ProductId,
                product.Name,
                displayUnitCode,
                total,
                HasActiveStock: true,
                DisplayUnitId: displayUnitId,
                SupportedUnits: supportedUnits,
                CategoryName: categoryName,
                CategorySortOrder: categorySortOrder);
        }

        // Branch B: tracked products whose default_location_id matches but have no active stock here
        // (zero quantity). Only add if not already covered by branch A.
        foreach (var product in allProducts)
        {
            if (!product.CanHoldStock) continue;
            if (product.DefaultLocationId is not { } defaultLoc) continue;
            if (defaultLoc.Value != locationId) continue;
            if (rows.ContainsKey(product.Id.Value)) continue; // already in branch A

            var displayUnitId = product.DefaultUnitId.Value;
            var displayUnitCode = unitCodesById.GetValueOrDefault(displayUnitId, "?");
            var productConversions = productConversionsById.GetValueOrDefault(product.Id.Value, []);
            var supportedUnits = BuildSupportedUnits(displayUnitId, allUnits, productConversions, unitCodesById);
            var (categoryName, categorySortOrder) = ResolveCategory(product.CategoryId, categoriesById);

            rows[product.Id.Value] = new TakeStockLocationProductRow(
                product.Id.Value,
                product.Name,
                displayUnitCode,
                RecordedQuantity: 0m,
                HasActiveStock: false,
                DisplayUnitId: displayUnitId,
                SupportedUnits: supportedUnits,
                CategoryName: categoryName,
                CategorySortOrder: categorySortOrder);
        }

        return rows.Values
            .OrderBy(r => r.ProductName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<TakeStockNoLocationRow>> ListNoLocationRowsAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return [];

        var household = HouseholdId.From(householdId);

        // All repositories share one scoped PantryDbContext (plantry-g3da.10) — every query below
        // must run sequentially, not concurrently (EF Core DbContext is single-threaded).
        var allStock = await stocks.ListForHouseholdAsync(household, ct);
        var allProducts = await products.ListActiveAsync(ct);
        var unitCodesById = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value, u => u.Code);

        // Only products with no default_location_id assigned.
        var noLocationProductIds = allProducts
            .Where(p => p.CanHoldStock && p.DefaultLocationId is null)
            .Select(p => p.Id.Value)
            .ToHashSet();

        var stockWithNoLocation = allStock
            .Where(s => noLocationProductIds.Contains(s.ProductId) && s.ActiveLotsFefo().Any())
            .ToList();

        var convertersByProduct = await conversions.ForProductsAsync(
            stockWithNoLocation.Select(s => s.ProductId), ct);

        var productsById = allProducts.ToDictionary(p => p.Id.Value);
        var rows = new List<TakeStockNoLocationRow>();

        foreach (var stock in stockWithNoLocation)
        {
            if (!productsById.TryGetValue(stock.ProductId, out var product)) continue;

            var activeLots = stock.ActiveLotsFefo().ToList();
            var converter = convertersByProduct.GetValueOrDefault(stock.ProductId)
                ?? new IdentityQuantityConverter();
            var displayUnitId = product.DefaultUnitId.Value;
            var displayUnitCode = unitCodesById.GetValueOrDefault(displayUnitId, "?");
            var total = SumInDisplayUnit(activeLots, displayUnitId, converter);

            rows.Add(new TakeStockNoLocationRow(
                stock.ProductId,
                product.Name,
                displayUnitCode,
                total,
                DisplayUnitId: displayUnitId));
        }

        return rows
            .OrderBy(r => r.ProductName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<TakeStockLotRow>> ListLotsAsync(
        Guid productId, Guid locationId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return [];

        var household = HouseholdId.From(householdId);
        var stock = await stocks.FindAsync(household, productId, ct);
        if (stock is null) return [];

        var unitCodes = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value, u => u.Code);

        return stock.ActiveLotsFefo()
            .Where(e => e.LocationId == locationId)
            .Select(e => new TakeStockLotRow(
                e.Id.Value,
                e.Quantity,
                unitCodes.GetValueOrDefault(e.UnitId, "?"),
                e.UnitId,
                e.ExpiryDate,
                e.IsOpen))
            .ToList();
    }

    public async Task<IReadOnlyList<TakeStockProductMatch>> SearchProductsAsync(
        string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var allProducts = await products.ListActiveAsync(ct);
        var unitCodesById = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value, u => u.Code);

        // Only tracked, non-parent products (CanHoldStock = true).
        var candidates = allProducts.Where(p => p.CanHoldStock).ToList();

        // Build a name→product map for lookup after ranking.
        var byName = candidates.ToDictionary(p => p.Name);

        // Rank via the shared ProductNameMatcher (same algorithm as CatalogProductReaderAdapter,
        // so results are consistent wherever the _ProductSearchCreateSheet is used).
        var hits = ProductNameMatcher.Rank(
            candidates.Select(p => (p.Id.Value, p.Name)),
            query.Trim());

        return hits
            .Select(h =>
            {
                var p = byName[h.Name];
                return new TakeStockProductMatch(
                    p.Id.Value,
                    p.Name,
                    unitCodesById.GetValueOrDefault(p.DefaultUnitId.Value, "?"),
                    p.DefaultLocationId?.Value ?? Guid.Empty,
                    p.DefaultUnitId.Value,
                    h.Score);
            })
            .ToList();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static decimal SumInDisplayUnit(
        IEnumerable<StockEntry> lots, Guid displayUnitId, IQuantityConverter converter)
    {
        var total = 0m;
        foreach (var lot in lots)
        {
            var converted = converter.Convert(lot.Quantity, lot.UnitId, displayUnitId);
            if (converted.IsSuccess) total += converted.Value;
        }
        return total;
    }

    /// <summary>
    /// Builds the ordered list of <see cref="TakeStockUnitOption"/> for the per-row unit selector (C10).
    /// Delegates reachability derivation to <see cref="UnitConverter.ReachableUnits"/>.
    /// </summary>
    private static IReadOnlyList<TakeStockUnitOption> BuildSupportedUnits(
        Guid defaultUnitId,
        IReadOnlyList<Unit> allUnits,
        IReadOnlyList<ProductConversion> productConversions,
        Dictionary<Guid, string> unitCodesById)
    {
        var reachableIds = UnitConverter.ReachableUnits(defaultUnitId, allUnits, productConversions);
        return reachableIds
            .Select(id => new TakeStockUnitOption(id, unitCodesById.GetValueOrDefault(id, "?")))
            .ToList();
    }

    /// <summary>
    /// Resolves a product's category name and store-layout sort order for the walk's category
    /// grouping (plantry-vvqt design item 6). Returns (null, int.MaxValue) when the product has no
    /// category, or when its category id doesn't resolve (a defensive fallback — category ids are a
    /// bare cross-row reference with no FK, same rationale as elsewhere in this adapter).
    /// </summary>
    private static (string? Name, int SortOrder) ResolveCategory(
        CategoryId? categoryId, Dictionary<Guid, Category> categoriesById)
    {
        if (categoryId is not { } id) return (null, int.MaxValue);
        return categoriesById.TryGetValue(id.Value, out var category)
            ? (category.Name, category.SortOrder)
            : (null, int.MaxValue);
    }

    /// <summary>Pass-through converter when no conversion table is available (same-unit lots).</summary>
    private sealed class IdentityQuantityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}
