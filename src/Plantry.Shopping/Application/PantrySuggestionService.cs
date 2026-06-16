namespace Plantry.Shopping.Application;

/// <summary>
/// Builds the "Running low in your pantry" suggestion list for the Shopping page
/// suggestions strip (plantry-48l). Orchestrates the <see cref="IShoppingPantryReader"/>
/// (to discover low/out products) and <see cref="IShoppingCatalogReader"/> (to resolve
/// product names and category info) without crossing bounded-context boundaries directly.
///
/// <para>The service is responsible for:</para>
/// <list type="bullet">
///   <item>Fetching all household pantry products with <c>IsLow = true</c> via the read port.</item>
///   <item>Excluding products whose id is present in <paramref name="onListProductIds"/>
///   (both unchecked and checked items are excluded — a checked item is "in progress").</item>
///   <item>Capping the result at <see cref="SuggestionCap"/> items (prototype shows 5).</item>
///   <item>Resolving product name and category info via the catalog read port.</item>
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
    /// present in <paramref name="onListProductIds"/>, enriched with catalog name and category hue.
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

        // Exclude products already on the list and cap at SuggestionCap.
        var eligible = lowStockProducts
            .Where(s => !onListProductIds.Contains(s.ProductId))
            .Take(SuggestionCap)
            .ToList();

        if (eligible.Count == 0)
            return [];

        // Resolve catalog summaries for product names and category info in one batch call.
        var eligibleIds = eligible.Select(s => s.ProductId).ToList();
        var summaries = await catalog.ResolveSummariesAsync(eligibleIds, ct);

        var suggestions = new List<PantrySuggestion>(eligible.Count);
        foreach (var stock in eligible)
        {
            summaries.TryGetValue(stock.ProductId, out var summary);
            suggestions.Add(new PantrySuggestion(
                ProductId: stock.ProductId,
                Name: summary?.Name ?? "(unknown)",
                OnHand: stock.OnHand,
                UnitCode: stock.UnitCode,
                IsLow: stock.IsLow,
                CategoryName: summary?.CategoryName,
                CategoryHue: summary?.CategoryHue));
        }

        return suggestions;
    }
}
