using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Inventory;

/// <summary>
/// Web-side adapter for <see cref="ITakeStockReader"/> (P4-3 / TS-10). Composes:
/// <list type="bullet">
/// <item><see cref="IProductStockRepository"/> — active lot data from the Inventory context.</item>
/// <item><see cref="IProductRepository"/>, <see cref="IUnitRepository"/>,
/// <see cref="ILocationRepository"/> — Catalog reference data for names and default locations.</item>
/// <item><see cref="IProductConversionProvider"/> — unit conversion for display-unit aggregation.</item>
/// </list>
/// Keeps both Inventory projects free of any direct Catalog dependency — the Port + Web-adapter seam
/// (same pattern as <see cref="CatalogReadFacade"/>).
/// </summary>
public sealed class TakeStockReaderAdapter(
    IProductStockRepository stocks,
    IProductRepository products,
    IUnitRepository units,
    ILocationRepository locations,
    IProductConversionProvider conversions,
    ITenantContext tenant) : ITakeStockReader
{
    public async Task<IReadOnlyList<TakeStockLocationRow>> ListLocationsAsync(CancellationToken ct = default)
    {
        var activeLocations = await locations.ListActiveAsync(ct);
        return activeLocations
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => new TakeStockLocationRow(l.Id.Value, l.Name))
            .ToList();
    }

    public async Task<IReadOnlyList<TakeStockLocationProductRow>> ListLocationRowsAsync(
        Guid locationId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return [];

        var household = HouseholdId.From(householdId);

        // Start the Inventory stock query (different DbContext — safe to run in parallel with Catalog).
        // The two Catalog queries (products, units) share the scoped CatalogDbContext and must be
        // sequential — EF Core DbContext is single-threaded (not safe for concurrent async queries).
        var allStockTask = stocks.ListForHouseholdAsync(household, ct);
        var allProducts = await products.ListActiveAsync(ct);
        var unitCodesById = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value, u => u.Code);
        var allStock = await allStockTask;

        // Indexed for fast lookup.
        var stockByProductId = allStock.ToDictionary(s => s.ProductId);

        // Batch-load converters for all products that have active lots in this location.
        var productIdsWithActiveStockHere = allStock
            .Where(s => s.ActiveLotsFefo().Any(e => e.LocationId == locationId))
            .Select(s => s.ProductId)
            .Distinct();
        var convertersByProduct = await conversions.ForProductsAsync(productIdsWithActiveStockHere, ct);

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

            rows[stock.ProductId] = new TakeStockLocationProductRow(
                stock.ProductId,
                product.Name,
                displayUnitCode,
                total,
                HasActiveStock: true,
                DisplayUnitId: displayUnitId);
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

            rows[product.Id.Value] = new TakeStockLocationProductRow(
                product.Id.Value,
                product.Name,
                displayUnitCode,
                RecordedQuantity: 0m,
                HasActiveStock: false,
                DisplayUnitId: displayUnitId);
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

        // Inventory stock query runs in parallel (different DbContext).
        // Catalog queries (products, units) are sequential — shared scoped CatalogDbContext is not
        // safe for concurrent async operations (EF Core single-threaded constraint).
        var allStockTask = stocks.ListForHouseholdAsync(household, ct);
        var allProducts = await products.ListActiveAsync(ct);
        var unitCodesById = (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value, u => u.Code);
        var allStock = await allStockTask;

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
                total));
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

        var normalizedQuery = query.Trim();

        // Only tracked, non-parent products.
        var candidates = allProducts.Where(p => p.CanHoldStock).ToList();

        // Exact match first, then contains, ordered alphabetically within each bucket.
        var exact = candidates
            .Where(p => p.Name.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

        var contains = candidates
            .Where(p => !p.Name.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                     && p.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

        return exact.Concat(contains)
            .Select(p => new TakeStockProductMatch(
                p.Id.Value,
                p.Name,
                unitCodesById.GetValueOrDefault(p.DefaultUnitId.Value, "?"),
                p.DefaultLocationId?.Value ?? Guid.Empty))
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

    /// <summary>Pass-through converter when no conversion table is available (same-unit lots).</summary>
    private sealed class IdentityQuantityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}
