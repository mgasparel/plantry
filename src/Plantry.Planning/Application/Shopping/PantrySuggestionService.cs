using Plantry.SharedKernel.Domain;

namespace Plantry.Planning.Application;

/// <summary>Builds the tiered "Pantry suggestions" list for the Shopping page.</summary>
public sealed class PantrySuggestionService(
    IShoppingPantryReader pantry,
    IShoppingCatalogReader catalog,
    IClock? clock = null)
{
    public const int SuggestionCap = 5;

    public async Task<IReadOnlyList<PantrySuggestion>> GetSuggestionsAsync(
        IReadOnlySet<Guid> onListProductIds, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime((clock ?? SystemClock.Instance).UtcNow.UtcDateTime);
        var lowStock = await pantry.GetLowStockProductsAsync(ct);
        var thresholded = lowStock.Where(s => s.HasLowStockThreshold).ToList();
        var staples = await pantry.GetFrequentStapleProductsAsync(today, ct);
        var stapleIds = staples.Select(s => s.ProductId).ToHashSet();

        var candidates = thresholded
            .Where(s => !onListProductIds.Contains(s.ProductId))
            .Select(s => (Stock: s, Tier: 1))
            .Concat(staples
                .Where(s => !onListProductIds.Contains(s.ProductId))
                .Select(s => (Stock: s, Tier: 2)))
            .Concat(lowStock
                .Where(s => !thresholded.Any(t => t.ProductId == s.ProductId)
                    && !stapleIds.Contains(s.ProductId) && !onListProductIds.Contains(s.ProductId))
                .Select(s => (Stock: s, Tier: 3)))
            .GroupBy(x => x.Stock.ProductId)
            .Select(g => g.OrderBy(x => x.Tier).First())
            .ToList();

        if (candidates.Count == 0)
            return [];

        var summaries = await catalog.ResolveSummariesAsync(candidates.Select(x => x.Stock.ProductId).ToList(), ct);
        return candidates
            .Select(x =>
            {
                summaries.TryGetValue(x.Stock.ProductId, out var summary);
                return new PantrySuggestion(x.Stock.ProductId, summary?.Name ?? "(unknown)", x.Stock.OnHand,
                    x.Stock.UnitCode, x.Stock.IsLow, summary?.CategoryName, summary?.CategoryHue, x.Tier == 2);
            })
            .OrderBy(s => s.Tier)
            .ThenBy(s => s.OnHand <= 0 ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(SuggestionCap)
            .ToList();
    }
}
