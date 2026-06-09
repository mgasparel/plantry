using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Inventory.Application;

/// <summary>How close a lot is to expiry, relative to the pantry threshold — drives the badge tone (SPEC §1d).</summary>
public enum ExpiryTone
{
    None,    // no dated lots
    Ok,      // beyond the "expiring soon" window
    Soon,    // within the window
    Expired, // already past
}

/// <summary>One product's row on the pantry list (SPEC §1a): name, quantity aggregated into the
/// product's display unit, and the soonest-expiry signal across its active lots.</summary>
public sealed record PantryListItem(
    Guid ProductId,
    string Name,
    string? CategoryName,
    string? LocationDisplay,
    bool IsVariant,
    decimal TotalQuantity,
    string DisplayUnitCode,
    int LotCount,
    DateOnly? SoonestExpiry,
    ExpiryTone ExpiryTone);

/// <summary>One physical lot on the product detail page (SPEC §1b).</summary>
public sealed record StockLotRow(
    StockEntryId EntryId,
    decimal Quantity,
    Guid UnitId,
    string UnitCode,
    DateOnly? ExpiryDate,
    string? LocationName,
    DateOnly? PurchasedAt,
    bool IsOpen);

/// <summary>One movement in a product's stock journal (SPEC §1b history).</summary>
public sealed record StockJournalRow(
    decimal Delta,
    string UnitCode,
    StockReason Reason,
    StockSourceType? SourceType,
    DateTimeOffset OccurredAt);

/// <summary>The product detail read model: live lots plus recent journal history.</summary>
public sealed record ProductStockDetail(
    Guid ProductId,
    string Name,
    string DisplayUnitCode,
    decimal TotalQuantity,
    IReadOnlyList<StockLotRow> Lots,
    IReadOnlyList<StockJournalRow> History);

/// <summary>
/// Builds the pantry list and product-stock detail read models. Inventory owns the lots/journal; the
/// Catalog names and per-product unit conversions arrive through the <see cref="ICatalogReadFacade"/>
/// and <see cref="IProductConversionProvider"/> ports (so this project stays <c>→ SharedKernel only</c>).
/// Quantities are aggregated into each product's display (default) unit via the same converter the
/// consume path uses; a lot whose unit cannot convert contributes 0 to the display total but is still
/// counted and dated — the authoritative fail-loud path is <see cref="ProductStock.Consume"/>.
/// </summary>
public sealed class InventoryQueryService(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant)
{
    /// <summary>Lots expiring within this many days of today render as <see cref="ExpiryTone.Soon"/> (SPEC §1d default).</summary>
    public const int ExpiringSoonDays = 7;

    public async Task<IReadOnlyList<PantryListItem>> ListPantryAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return [];
        var allStock = await stocks.ListForHouseholdAsync(HouseholdId.From(householdId), ct);
        var productsById = (await catalog.ListProductsAsync(ct)).ToDictionary(p => p.Id);
        var locationNames = await catalog.GetLocationNamesAsync(ct);
        var unitCodes = await catalog.GetUnitCodesAsync(ct);
        var today = Today();

        var convertersByProduct = await conversions.ForProductsAsync(allStock.Select(s => s.ProductId), ct);

        var items = new List<PantryListItem>(allStock.Count);
        foreach (var stock in allStock)
        {
            var activeLots = stock.ActiveLotsFefo().ToList();
            if (activeLots.Count == 0) continue; // empty shells (all lots depleted) are not pantry rows

            if (!productsById.TryGetValue(stock.ProductId, out var product))
                continue; // product archived/removed from catalog — skip rather than render "?"

            var converter = convertersByProduct[stock.ProductId];
            var (total, displayUnitCode) = DisplayQuantity(activeLots, product.DefaultUnitId, product.DefaultUnitCode, converter, unitCodes);
            var soonest = activeLots.Where(l => l.ExpiryDate is not null).Min(l => l.ExpiryDate);

            var distinctLocations = activeLots.Select(l => l.LocationId).Distinct().ToList();
            var locationDisplay = distinctLocations.Count switch
            {
                0 => null,
                1 => locationNames.GetValueOrDefault(distinctLocations[0]),
                _ => "Multiple",
            };

            items.Add(new PantryListItem(
                stock.ProductId,
                product.Name,
                product.CategoryName,
                locationDisplay,
                product.IsVariant,
                total,
                displayUnitCode,
                activeLots.Count,
                soonest,
                ToneFor(soonest, today)));
        }

        return items
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ProductStockDetail?> FindDetailAsync(Guid productId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return null;

        var stock = await stocks.FindWithHistoryAsync(HouseholdId.From(householdId), productId, ct);
        if (stock is null) return null;

        var product = await catalog.FindProductAsync(productId, ct);
        var unitCodes = await catalog.GetUnitCodesAsync(ct);
        var locationNames = await catalog.GetLocationNamesAsync(ct);
        var converter = await conversions.ForProductAsync(productId, ct);

        var displayUnitId = product?.DefaultUnitId;
        var activeLots = stock.ActiveLotsFefo().ToList();
        var (total, displayUnitCode) = displayUnitId is { } duid
            ? DisplayQuantity(activeLots, duid, product!.DefaultUnitCode, converter, unitCodes)
            : (0m, "?");

        var lots = activeLots
            .Select(l => new StockLotRow(
                l.Id,
                l.Quantity,
                l.UnitId,
                unitCodes.GetValueOrDefault(l.UnitId, "?"),
                l.ExpiryDate,
                locationNames.GetValueOrDefault(l.LocationId),
                l.PurchasedAt,
                l.IsOpen))
            .ToList();

        var history = stock.Journal
            .OrderByDescending(j => j.OccurredAt)
            .Select(j => new StockJournalRow(
                j.Delta,
                unitCodes.GetValueOrDefault(j.UnitId, "?"),
                j.Reason,
                j.SourceType,
                j.OccurredAt))
            .ToList();

        return new ProductStockDetail(
            productId,
            product?.Name ?? "Unknown product",
            displayUnitCode,
            total,
            lots,
            history);
    }

    /// <summary>
    /// Returns the quantity and unit code to display for a set of active lots.
    /// When all lots convert to <paramref name="defaultUnitId"/> that result is used.
    /// When conversion fails entirely (e.g. "ea" lots on a "g" product), falls back to the
    /// lots' own unit so the user sees "1 ea" rather than a misleading "0 g".
    /// </summary>
    private static (decimal Total, string UnitCode) DisplayQuantity(
        IReadOnlyList<StockEntry> activeLots, Guid defaultUnitId, string defaultUnitCode,
        IQuantityConverter converter, IReadOnlyDictionary<Guid, string> unitCodes)
    {
        var total = SumInDisplayUnit(activeLots, defaultUnitId, converter);
        if (total > 0 || activeLots.Count == 0)
            return (total, defaultUnitCode);

        // Conversion to the default unit yielded nothing — fall back to the lot's own unit.
        var distinctUnitIds = activeLots.Select(l => l.UnitId).Distinct().ToList();
        var fallbackId = distinctUnitIds[0];
        var fallbackCode = unitCodes.GetValueOrDefault(fallbackId, "?");
        return distinctUnitIds.Count == 1
            ? (activeLots.Sum(l => l.Quantity), fallbackCode)
            : (activeLots.Sum(l => l.Quantity), "?"); // mixed incompatible units — honest but rare
    }

    private static decimal SumInDisplayUnit(IEnumerable<StockEntry> lots, Guid displayUnitId, IQuantityConverter converter)
    {
        var total = 0m;
        foreach (var lot in lots)
        {
            var converted = converter.Convert(lot.Quantity, lot.UnitId, displayUnitId);
            if (converted.IsSuccess) total += converted.Value;
        }
        return total;
    }

    private static ExpiryTone ToneFor(DateOnly? soonest, DateOnly today)
    {
        if (soonest is not { } date) return ExpiryTone.None;
        if (date < today) return ExpiryTone.Expired;
        if (date <= today.AddDays(ExpiringSoonDays)) return ExpiryTone.Soon;
        return ExpiryTone.Ok;
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
}
