using Plantry.Shopping.Application;

namespace Plantry.Tests.Unit.Shopping.Application;

/// <summary>
/// Unit tests for <see cref="PantrySuggestionService"/> (plantry-48l acceptance criteria).
/// Verifies the exclusion + cap logic:
/// <list type="bullet">
///   <item>Products already on the active list are excluded from suggestions.</item>
///   <item>Suggestions are capped at <see cref="PantrySuggestionService.SuggestionCap"/> (5).</item>
///   <item>Non-low products are not returned.</item>
///   <item>Empty pantry produces empty suggestions.</item>
/// </list>
/// </summary>
public sealed class PantrySuggestionServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PantrySuggestionService BuildService(
        FakeShoppingPantryReader pantry,
        FakeShoppingCatalogReaderWithSummaries? catalog = null)
        => new(pantry, catalog ?? new FakeShoppingCatalogReaderWithSummaries());

    /// <summary>
    /// Builds a restock candidate (a product <see cref="PantrySuggestionService"/> should surface).
    /// IsLow is running-low only (plantry-43y): true iff onHand &gt; 0 (running low), false when
    /// onHand = 0 (out — which still surfaces as a candidate via the OnHand ≤ 0 predicate).
    /// </summary>
    private static ShoppingPantryStockLevel LowStock(Guid productId, decimal onHand = 0m) =>
        new(productId, OnHand: onHand, UnitCode: "ea", IsLow: onHand > 0m);

    // ── Exclusion logic ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GetSuggestions — product already on list is excluded")]
    public async Task GetSuggestions_ProductOnList_IsExcluded()
    {
        var onListId = Guid.NewGuid();
        var offListId = Guid.NewGuid();

        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(onListId,  LowStock(onListId));
        pantry.RegisterStock(offListId, LowStock(offListId));

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid> { onListId });

        // Only the off-list product should be suggested.
        Assert.Single(suggestions);
        Assert.Equal(offListId, suggestions[0].ProductId);
    }

    [Fact(DisplayName = "GetSuggestions — all products on list produces empty suggestions")]
    public async Task GetSuggestions_AllOnList_ReturnsEmpty()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(id1, LowStock(id1));
        pantry.RegisterStock(id2, LowStock(id2));

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid> { id1, id2 });

        Assert.Empty(suggestions);
    }

    [Fact(DisplayName = "GetSuggestions — empty on-list set returns all low products")]
    public async Task GetSuggestions_EmptyOnList_ReturnsAllLowProducts()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(id1, LowStock(id1));
        pantry.RegisterStock(id2, LowStock(id2));

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Equal(2, suggestions.Count);
    }

    // ── Cap logic ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GetSuggestions — more than 5 eligible products are capped at 5")]
    public async Task GetSuggestions_MoreThanCap_CapsAtFive()
    {
        var pantry = new FakeShoppingPantryReader();
        for (var i = 0; i < 8; i++)
            pantry.RegisterStock(Guid.NewGuid(), LowStock(Guid.NewGuid()));

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Equal(PantrySuggestionService.SuggestionCap, suggestions.Count);
    }

    [Fact(DisplayName = "GetSuggestions — exactly 5 eligible products are not truncated")]
    public async Task GetSuggestions_ExactlyFive_AllReturned()
    {
        var pantry = new FakeShoppingPantryReader();
        for (var i = 0; i < PantrySuggestionService.SuggestionCap; i++)
            pantry.RegisterStock(Guid.NewGuid(), LowStock(Guid.NewGuid()));

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Equal(PantrySuggestionService.SuggestionCap, suggestions.Count);
    }

    [Fact(DisplayName = "GetSuggestions — cap applies AFTER exclusion (not before)")]
    public async Task GetSuggestions_CapAppliedAfterExclusion()
    {
        // Register 6 low products; put 3 on the list; expect 3 suggestions (all remaining), not 5.
        var allIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var pantry = new FakeShoppingPantryReader();
        foreach (var id in allIds)
            pantry.RegisterStock(id, LowStock(id));

        // Put 3 on the list.
        var onListIds = allIds.Take(3).ToHashSet();

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(onListIds);

        // The remaining 3 are all eligible; cap of 5 doesn't truncate here.
        Assert.Equal(3, suggestions.Count);
        Assert.All(suggestions, s => Assert.DoesNotContain(s.ProductId, onListIds));
    }

    // ── Non-low products ──────────────────────────────────────────────────────

    [Fact(DisplayName = "GetSuggestions — non-low products are not included")]
    public async Task GetSuggestions_NonLowProduct_NotIncluded()
    {
        var lowId = Guid.NewGuid();
        var normalId = Guid.NewGuid();

        var pantry = new FakeShoppingPantryReader();
        // outId is a restock candidate (out → IsLow false, surfaced via OnHand ≤ 0);
        // normalId is genuinely in-stock (OnHand > 0, not low) and must NOT be suggested.
        pantry.RegisterStock(lowId,    new ShoppingPantryStockLevel(lowId,    OnHand: 0m,   UnitCode: "ea", IsLow: false));
        pantry.RegisterStock(normalId, new ShoppingPantryStockLevel(normalId, OnHand: 3m,   UnitCode: "ea", IsLow: false));

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        // GetLowStockProductsAsync surfaces running-low ∪ out; the in-stock product is excluded.
        Assert.Single(suggestions);
        Assert.Equal(lowId, suggestions[0].ProductId);
    }

    // ── Empty pantry ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "GetSuggestions — empty pantry returns empty list")]
    public async Task GetSuggestions_EmptyPantry_ReturnsEmpty()
    {
        var pantry = new FakeShoppingPantryReader();
        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());
        Assert.Empty(suggestions);
    }

    // ── Catalog enrichment ────────────────────────────────────────────────────

    [Fact(DisplayName = "GetSuggestions — catalog name and category hue are resolved for suggestions")]
    public async Task GetSuggestions_CatalogEnrichment_NameAndHueResolved()
    {
        var productId = Guid.NewGuid();
        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(productId, LowStock(productId, onHand: 0m));

        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(productId, new ShoppingProductSummary(productId, "Whole Milk", "Dairy", CategoryHue: 200));

        var svc = BuildService(pantry, catalog);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Single(suggestions);
        var suggestion = suggestions[0];
        Assert.Equal("Whole Milk", suggestion.Name);
        Assert.Equal("Dairy", suggestion.CategoryName);
        Assert.Equal(200, suggestion.CategoryHue);
    }

    [Fact(DisplayName = "GetSuggestions — StockLabel returns 'out' when OnHand is zero")]
    public async Task GetSuggestions_ZeroOnHand_StockLabelIsOut()
    {
        var productId = Guid.NewGuid();
        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(productId, LowStock(productId, onHand: 0m));

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Single(suggestions);
        Assert.Equal("out", suggestions[0].StockLabel);
    }

    [Fact(DisplayName = "GetSuggestions — StockLabel returns 'N unit left' when OnHand is positive")]
    public async Task GetSuggestions_PositiveOnHand_StockLabelIncludesQuantityAndUnit()
    {
        var productId = Guid.NewGuid();
        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(productId,
            new ShoppingPantryStockLevel(productId, OnHand: 0.5m, UnitCode: "L", IsLow: true));

        var svc = BuildService(pantry);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Single(suggestions);
        Assert.Equal("0.5 L left", suggestions[0].StockLabel);
    }

    // ── Deterministic ordering (plantry-14q) ──────────────────────────────────

    [Fact(DisplayName = "GetSuggestions — out-of-stock items (OnHand<=0) sort before low items")]
    public async Task GetSuggestions_OutOfStock_SortedBeforeLow()
    {
        var lowId = Guid.NewGuid();
        var outId = Guid.NewGuid();

        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(lowId, new ShoppingPantryStockLevel(lowId, OnHand: 0.5m, UnitCode: "ea", IsLow: true));
        pantry.RegisterStock(outId, new ShoppingPantryStockLevel(outId, OnHand: 0m,   UnitCode: "ea", IsLow: false));

        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(lowId, new ShoppingProductSummary(lowId, "Apple", null, null));
        catalog.RegisterSummary(outId, new ShoppingProductSummary(outId, "Banana", null, null));

        var svc = BuildService(pantry, catalog);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Equal(2, suggestions.Count);
        // out-of-stock "Banana" must come before low-stock "Apple"
        Assert.Equal(outId, suggestions[0].ProductId);
        Assert.Equal(lowId, suggestions[1].ProductId);
    }

    [Fact(DisplayName = "GetSuggestions — within same stock tier, items are ordered by name ascending")]
    public async Task GetSuggestions_WithinTier_OrderedByNameAscending()
    {
        var idC = Guid.NewGuid();
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(idC, new ShoppingPantryStockLevel(idC, OnHand: 0m, UnitCode: "ea", IsLow: false));
        pantry.RegisterStock(idA, new ShoppingPantryStockLevel(idA, OnHand: 0m, UnitCode: "ea", IsLow: false));
        pantry.RegisterStock(idB, new ShoppingPantryStockLevel(idB, OnHand: 0m, UnitCode: "ea", IsLow: false));

        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(idC, new ShoppingProductSummary(idC, "Cheddar", null, null));
        catalog.RegisterSummary(idA, new ShoppingProductSummary(idA, "Apple",   null, null));
        catalog.RegisterSummary(idB, new ShoppingProductSummary(idB, "Butter",  null, null));

        var svc = BuildService(pantry, catalog);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Equal(3, suggestions.Count);
        Assert.Equal("Apple",   suggestions[0].Name);
        Assert.Equal("Butter",  suggestions[1].Name);
        Assert.Equal("Cheddar", suggestions[2].Name);
    }

    [Fact(DisplayName = "GetSuggestions — Take(5) cap is applied after ordering, not before")]
    public async Task GetSuggestions_CapAppliedAfterOrdering_CorrectTop5Selected()
    {
        // 7 out-of-stock products with names A–G; expected top 5 are A, B, C, D, E.
        var names = new[] { "G", "F", "E", "D", "C", "B", "A" };
        var pantry = new FakeShoppingPantryReader();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        foreach (var name in names)
        {
            var id = Guid.NewGuid();
            pantry.RegisterStock(id, new ShoppingPantryStockLevel(id, OnHand: 0m, UnitCode: "ea", IsLow: false));
            catalog.RegisterSummary(id, new ShoppingProductSummary(id, name, null, null));
        }

        var svc = BuildService(pantry, catalog);
        var suggestions = await svc.GetSuggestionsAsync(new HashSet<Guid>());

        Assert.Equal(PantrySuggestionService.SuggestionCap, suggestions.Count);
        // After ordering by name ascending and taking 5, we expect A, B, C, D, E.
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, suggestions.Select(s => s.Name).ToArray());
    }
}
