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
    ExpiryTone ExpiryTone,
    /// <summary>Hue in degrees (0–359) from the product's category. Null when uncategorised or no hue assigned.</summary>
    int? CategoryHue = null,
    /// <summary>The configured low stock threshold for this product/household pair. Null means no threshold set.</summary>
    decimal? LowStockThreshold = null,
    /// <summary>True when <see cref="TotalQuantity"/> ≤ <see cref="LowStockThreshold"/> and a threshold is set.</summary>
    bool IsRunningLow = false);

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
/// <param name="JournalId">
/// The journal row's own id (receipt-intake-history.md H3) — the provenance-chip correlation key: the
/// composition-side <c>IStockProvenanceReader</c> keys its resolved chip dictionary by this value, and it
/// also doubles as the legacy-row reverse-lookup key (H2) when <see cref="SourceRef"/> is null.
/// </param>
/// <param name="SourceRef">
/// The orthogonal "what triggered this" reference (DM-14) — an <c>ImportLine.Id</c> for a post-H1 Intake
/// row, a <c>CookEvent.Id</c> for a Cook row, null for Manual or a pre-H1 legacy Intake row. Exposed here
/// (raw, untyped) purely so the Web-side provenance reader can resolve it without Inventory taking any
/// dependency on Intake/Recipes (Gate 2 — IDs only).
/// </param>
public sealed record StockJournalRow(
    Guid JournalId,
    decimal Delta,
    string UnitCode,
    StockReason Reason,
    StockSourceType? SourceType,
    Guid? SourceRef,
    DateTimeOffset OccurredAt);

/// <summary>
/// One row in the expiring-soon widget on the Today page (SPEC Page 0 §0d).
/// Products ordered soonest-first; expired lots lead. DaysLeft is 0 for same-day and positive
/// for future dates; IsExpired true when the soonest lot is already past today.
/// </summary>
public sealed record ExpiringSoonItem(
    Guid ProductId,
    string Name,
    decimal TotalQuantity,
    string DisplayUnitCode,
    /// <summary>Location sub-line, e.g. "Pantry" or "Multiple". Null when no location set.</summary>
    string? LocationDisplay,
    DateOnly SoonestExpiry,
    /// <summary>Days until SoonestExpiry, 0 for same-day, positive for future; expired items have DaysLeft = 0 and IsExpired = true.</summary>
    int DaysLeft,
    bool IsExpired);

/// <summary>The product detail read model: live lots plus recent journal history.</summary>
public sealed record ProductStockDetail(
    Guid ProductId,
    string Name,
    string DisplayUnitCode,
    decimal TotalQuantity,
    IReadOnlyList<StockLotRow> Lots,
    IReadOnlyList<StockJournalRow> History,
    string? CategoryName = null,
    /// <summary>Hue in degrees (0–359) from the product's category. Null when uncategorised or no hue assigned.</summary>
    int? CategoryHue = null,
    /// <summary>The configured low stock threshold for this product/household pair. Null means no threshold set.</summary>
    decimal? LowStockThreshold = null,
    /// <summary>True when <see cref="TotalQuantity"/> ≤ <see cref="LowStockThreshold"/> and a threshold is set.</summary>
    bool IsRunningLow = false);

/// <summary>
/// Builds the pantry list and product-stock detail read models. Inventory owns the lots/journal; the
/// Catalog names and per-product unit conversions arrive through the <see cref="ICatalogReadFacade"/>
/// and <see cref="IProductConversionProvider"/> ports (so this project stays <c>→ SharedKernel only</c>).
/// Quantities are aggregated into each product's display (default) unit via the same converter the
/// consume path uses; a lot whose unit cannot convert contributes 0 to the display total but is still
/// counted and dated — the authoritative fail-loud path is <see cref="ProductStock.Consume"/>.
/// </summary>
public class InventoryQueryService(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
    IExpiringSoonHorizon horizon,
    IClock clock,
    ITenantContext tenant)
{
    /// <summary>Maximum number of rows returned by <see cref="ExpiringSoonAsync"/> (top-N soonest-first).</summary>
    public const int ExpiringSoonMaxItems = 10;

    public async Task<IReadOnlyList<PantryListItem>> ListPantryAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return [];
        var allStock = await stocks.ListForHouseholdAsync(HouseholdId.From(householdId), ct);
        var productsById = (await catalog.ListProductsAsync(ct)).ToDictionary(p => p.Id);
        var locationNames = await catalog.GetLocationNamesAsync(ct);
        var unitCodes = await catalog.GetUnitCodesAsync(ct);
        var today = Today();
        var expiringSoonDays = await horizon.GetDaysAsync(ct);

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
                ToneFor(soonest, today, expiringSoonDays),
                CategoryHue: product.CategoryHue,
                LowStockThreshold: stock.LowStockThreshold,
                IsRunningLow: stock.IsRunningLow(total)));
        }

        return items
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The number of products currently in the pantry — the row count <see cref="ListPantryAsync"/>
    /// would produce, computed without materializing names, locations, or unit conversions. Applies the
    /// same inclusion predicate: a stock counts iff it has at least one active lot and its product still
    /// exists in the catalog. Returns 0 when there is no household in the tenant context.
    /// </summary>
    public virtual async Task<int> CountInStockAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return 0;
        var allStock = await stocks.ListForHouseholdAsync(HouseholdId.From(householdId), ct);
        var knownProductIds = (await catalog.ListProductsAsync(ct)).Select(p => p.Id).ToHashSet();

        return allStock.Count(stock =>
            knownProductIds.Contains(stock.ProductId) && stock.ActiveLotsFefo().Any());
    }

    /// <summary>
    /// Returns up to <see cref="ExpiringSoonMaxItems"/> products with active lots whose soonest expiry
    /// is within the household's configured "expiring soon" horizon (or already past), ordered soonest-first
    /// (expired first, then by date ascending). Only products with at least one dated lot are included.
    /// Household-scoped via the tenant context.
    /// </summary>
    public virtual async Task<IReadOnlyList<ExpiringSoonItem>> ExpiringSoonAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return [];
        var allStock = await stocks.ListForHouseholdAsync(HouseholdId.From(householdId), ct);
        var productsById = (await catalog.ListProductsAsync(ct)).ToDictionary(p => p.Id);
        var locationNames = await catalog.GetLocationNamesAsync(ct);
        var unitCodes = await catalog.GetUnitCodesAsync(ct);
        var today = Today();
        var windowEnd = today.AddDays(await horizon.GetDaysAsync(ct));

        var convertersByProduct = await conversions.ForProductsAsync(allStock.Select(s => s.ProductId), ct);

        var items = new List<ExpiringSoonItem>();
        foreach (var stock in allStock)
        {
            var activeLots = stock.ActiveLotsFefo().ToList();
            if (activeLots.Count == 0) continue;

            if (!productsById.TryGetValue(stock.ProductId, out var product))
                continue;

            // Only include products with at least one dated lot in/past the expiry window.
            // Predicate shared with CountExpiringSoonAsync so the two paths can't drift.
            var soonest = SoonestDatedExpiry(activeLots);
            if (!IsWithinExpiryWindow(soonest, windowEnd)) continue;

            var converter = convertersByProduct[stock.ProductId];
            var (total, displayUnitCode) = DisplayQuantity(activeLots, product.DefaultUnitId, product.DefaultUnitCode, converter, unitCodes);

            var distinctLocations = activeLots.Select(l => l.LocationId).Distinct().ToList();
            var locationDisplay = distinctLocations.Count switch
            {
                0 => null,
                1 => locationNames.GetValueOrDefault(distinctLocations[0]),
                _ => "Multiple",
            };

            var isExpired = soonest.Value < today;
            var daysLeft = isExpired ? 0 : (soonest.Value.ToDateTime(TimeOnly.MinValue) - today.ToDateTime(TimeOnly.MinValue)).Days;

            items.Add(new ExpiringSoonItem(
                stock.ProductId,
                product.Name,
                total,
                displayUnitCode,
                locationDisplay,
                soonest.Value,
                daysLeft,
                isExpired));
        }

        return items
            .OrderBy(i => i.SoonestExpiry)
            .Take(ExpiringSoonMaxItems)
            .ToList();
    }

    /// <summary>
    /// The number of products whose soonest dated active-lot expiry falls within the household's
    /// configured "expiring soon" horizon (already-past dates included) — the same inclusion predicate
    /// as <see cref="ExpiringSoonAsync"/> but WITHOUT the <see cref="ExpiringSoonMaxItems"/> cap and
    /// without building the display projection. Equals <see cref="ExpiringSoonAsync"/>'s count whenever
    /// that count is under the cap, and exceeds it when more qualify. Undated lots never qualify.
    /// Returns 0 when there is no household in the tenant context.
    /// </summary>
    public virtual async Task<int> CountExpiringSoonAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return 0;
        var allStock = await stocks.ListForHouseholdAsync(HouseholdId.From(householdId), ct);
        var knownProductIds = (await catalog.ListProductsAsync(ct)).Select(p => p.Id).ToHashSet();
        var windowEnd = Today().AddDays(await horizon.GetDaysAsync(ct));

        return allStock.Count(stock =>
        {
            if (!knownProductIds.Contains(stock.ProductId)) return false;
            var activeLots = stock.ActiveLotsFefo().ToList();
            return activeLots.Count > 0 && IsWithinExpiryWindow(SoonestDatedExpiry(activeLots), windowEnd);
        });
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
                j.Id.Value,
                j.Delta,
                unitCodes.GetValueOrDefault(j.UnitId, "?"),
                j.Reason,
                j.SourceType,
                j.SourceRef,
                j.OccurredAt))
            .ToList();

        return new ProductStockDetail(
            productId,
            product?.Name ?? "Unknown product",
            displayUnitCode,
            total,
            lots,
            history,
            CategoryName: product?.CategoryName,
            CategoryHue: product?.CategoryHue,
            LowStockThreshold: stock.LowStockThreshold,
            IsRunningLow: stock.IsRunningLow(total));
    }

    /// <summary>
    /// Returns the quantity and unit code to display for a set of active lots.
    /// When all lots convert to <paramref name="defaultUnitId"/> that result is used.
    /// When conversion fails entirely (e.g. "ea" lots on a "g" product), falls back to the
    /// lots' own unit so the user sees "1 ea" rather than a misleading "0 g".
    ///
    /// <para>Public so <c>ShoppingPantryReaderAdapter</c> (the Shopping→Inventory ACL adapter,
    /// ADR-002) can share this exact fallback semantics rather than re-deriving its own
    /// aggregation — a product whose only active lots fail unit conversion must never be
    /// reported as zero/"out" by one context while the other shows its real quantity
    /// (plantry-2hfi). <see cref="SumInDisplayUnit"/> is similarly shared with the consume path.</para>
    /// </summary>
    public static (decimal Total, string UnitCode) DisplayQuantity(
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

    /// <summary>Shared with <see cref="ConsumeStockCommand"/> — both paths must agree on the on-hand
    /// sum so the low-stock counter and the pantry UI always agree on the running-low state.</summary>
    internal static decimal SumInDisplayUnit(IEnumerable<StockEntry> lots, Guid displayUnitId, IQuantityConverter converter)
    {
        var total = 0m;
        foreach (var lot in lots)
        {
            var converted = converter.Convert(lot.Quantity, lot.UnitId, displayUnitId);
            if (converted.IsSuccess) total += converted.Value;
        }
        return total;
    }

    /// <summary>The soonest dated-lot expiry among a set of active lots, or null when none carry a date.
    /// Shared by <see cref="ExpiringSoonAsync"/> and <see cref="CountExpiringSoonAsync"/> so the two paths
    /// compute "which lots count and how the soonest date is chosen" identically and can't drift.</summary>
    private static DateOnly? SoonestDatedExpiry(IReadOnlyList<StockEntry> activeLots) =>
        activeLots.Where(l => l.ExpiryDate is not null).Min(l => l.ExpiryDate);

    /// <summary>A product is "expiring soon" when its soonest dated lot falls on or before the window end
    /// (already-past dates included). An undated product (null soonest) never qualifies.</summary>
    private static bool IsWithinExpiryWindow(DateOnly? soonest, DateOnly windowEnd) =>
        soonest is { } date && date <= windowEnd;

    private static ExpiryTone ToneFor(DateOnly? soonest, DateOnly today, int expiringSoonDays)
    {
        if (soonest is not { } date) return ExpiryTone.None;
        if (date < today) return ExpiryTone.Expired;
        if (date <= today.AddDays(expiringSoonDays)) return ExpiryTone.Soon;
        return ExpiryTone.Ok;
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
}
