namespace Plantry.Shopping.Application;

/// <summary>
/// Builds the "Running low in your pantry" suggestion list for the Shopping page
/// suggestions strip (plantry-48l / plantry-14q). Orchestrates the <see cref="IShoppingPantryReader"/>
/// (to discover low/out products) and <see cref="IShoppingCatalogReader"/> (to resolve
/// product names and category info) without crossing bounded-context boundaries directly.
///
/// <para>The service is responsible for:</para>
/// <list type="bullet">
///   <item>Fetching all household pantry products with <c>IsLow = true</c> via the read port.</item>
///   <item>Excluding products whose id is present in <paramref name="onListProductIds"/>
///   (both unchecked and checked items are excluded — a checked item is "in progress").</item>
///   <item>Resolving catalog summaries for ALL eligible products (before the cap), so ordering
///   by name is deterministic regardless of pantry data order.</item>
///   <item>Ordering: out-of-stock first (<c>OnHand &lt;= 0</c>), then product name ascending.</item>
///   <item>Capping the result at <see cref="SuggestionCap"/> items AFTER ordering.</item>
/// </list>
/// </summary>
public sealed class PantrySuggestionService(
    IShoppingPantryReader pantry,
    IShoppingCatalogReader catalog)
{
    /// <summary>Maximum number of suggestion chips rendered in the strip (matches prototype).</summary>
    public const int SuggestionCap = 5;

    /// <summary>
    /// Returns up to <see cref="SuggestionCap"/> pantry suggestions: low/out products NOT
    /// present in <paramref name="onListProductIds"/>, ordered out-of-stock first then name
    /// ascending, enriched with catalog name and category hue.
    /// </summary>
    /// <param name="onListProductIds">
    /// Product ids already on the active shopping list (unchecked and checked). Items whose
    /// product id appears here are excluded from suggestions.
    /// </param>
    public async Task<IReadOnlyList<PantrySuggestion>> GetSuggestionsAsync(
        IReadOnlySet<Guid> onListProductIds,
        CancellationToken ct = default)
    {
        // Fetch all low-stock products from the pantry read port.
        var lowStockProducts = await pantry.GetLowStockProductsAsync(ct);

        // Exclude products already on the list — do NOT cap yet; we need names to order by.
        var eligible = lowStockProducts
            .Where(s => !onListProductIds.Contains(s.ProductId))
            .ToList();

        if (eligible.Count == 0)
            return [];

        // Resolve catalog summaries for ALL eligible products before the cap so that the
        // name-ascending ordering is applied across the full candidate set, not just the
        // first SuggestionCap entries (plantry-14q deterministic ordering requirement).
        var eligibleIds = eligible.Select(s => s.ProductId).ToList();
        var summaries = await catalog.ResolveSummariesAsync(eligibleIds, ct);

        // Build enriched suggestions, then apply deterministic ordering:
        //   1. Out-of-stock first (OnHand <= 0)
        //   2. Product name ascending (stable across rerenders)
        //   3. Take(SuggestionCap) applied AFTER ordering
        var suggestions = eligible
            .Select(stock =>
            {
                summaries.TryGetValue(stock.ProductId, out var summary);
                return new PantrySuggestion(
                    ProductId: stock.ProductId,
                    Name: summary?.Name ?? "(unknown)",
                    OnHand: stock.OnHand,
                    UnitCode: stock.UnitCode,
                    IsLow: stock.IsLow,
                    CategoryName: summary?.CategoryName,
                    CategoryHue: summary?.CategoryHue);
            })
            .OrderBy(s => s.OnHand <= 0 ? 0 : 1)   // out-of-stock first
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(SuggestionCap)
            .ToList();

        return suggestions;
    }
}
