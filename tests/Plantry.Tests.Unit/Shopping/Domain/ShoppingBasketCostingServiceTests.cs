using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Tests.Unit.Shopping.Application;

namespace Plantry.Tests.Unit.Shopping.Domain;

/// <summary>
/// L1 unit tests for <see cref="ShoppingBasketCostingService"/> (plantry-e016, stats-injection appendix:
/// shopping list — estimated basket cost). Covers the three per-line pricing outcomes: exact (confident),
/// quantity/unit-uncertain (range), and no price history at all (footnoted, never guessed). Reuses the
/// shared <see cref="FakeShoppingCatalogReader"/> (unit conversion table) from the Application test tree.
/// </summary>
public sealed class ShoppingBasketCostingServiceTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();
    private static readonly Guid UnitId = Guid.CreateVersion7();
    private static readonly Guid OtherUnitId = Guid.CreateVersion7();
    private static readonly IClock Clock = SystemClock.Instance;

    private static ShoppingBasketCostingService BuildService(
        FakeShoppingPriceReader prices, FakeShoppingCatalogReader? catalog = null, IClock? clock = null) =>
        new(prices, catalog ?? new FakeShoppingCatalogReader(), clock ?? Clock);

    // ── Exact line cost (confident) ───────────────────────────────────────────

    [Fact(DisplayName = "Estimate — priced item, same unit as observation: exact line cost, Low == High")]
    public async Task Estimate_SameUnit_ExactLineCost_LowEqualsHigh()
    {
        var list = ShoppingList.Create(Household, Clock);
        list.AddItem(ProductA, quantity: 3m, unitId: UnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);

        var prices = new FakeShoppingPriceReader();
        prices.RegisterPrice(ProductA, new ShoppingPriceEstimate(ProductA, Price: 4.00m, Quantity: 2m, UnitId: UnitId));

        var svc = BuildService(prices);
        var estimate = await svc.EstimateAsync(list.Items);

        // unitPrice = 4.00 / 2 = 2.00 per unit; 3 units → 6.00.
        Assert.True(estimate.HasEstimate);
        Assert.False(estimate.IsRange);
        Assert.Equal(6.00m, estimate.Low);
        Assert.Equal(6.00m, estimate.High);
        Assert.Equal(0, estimate.UnpricedCount);
    }

    [Fact(DisplayName = "Estimate — priced item, convertible unit: exact line cost using the converted rate")]
    public async Task Estimate_ConvertibleUnit_ExactLineCost()
    {
        var list = ShoppingList.Create(Household, Clock);
        list.AddItem(ProductA, quantity: 500m, unitId: OtherUnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);

        var prices = new FakeShoppingPriceReader();
        // $2 for 1 UnitId; 1 UnitId converts to 1000 OtherUnitId (e.g. kg → g).
        prices.RegisterPrice(ProductA, new ShoppingPriceEstimate(ProductA, Price: 2.00m, Quantity: 1m, UnitId: UnitId));
        var catalog = new FakeShoppingCatalogReader();
        catalog.RegisterConversion(UnitId, OtherUnitId, ProductA, convertedAmount: 1000m);

        var svc = BuildService(prices, catalog);
        var estimate = await svc.EstimateAsync(list.Items);

        // costPerItemUnit = 2.00 / 1000 = 0.002; × 500 = 1.00.
        Assert.Equal(1.00m, estimate.Low);
        Assert.Equal(1.00m, estimate.High);
        Assert.False(estimate.IsRange);
    }

    // ── Uncertain lines (range) ───────────────────────────────────────────────

    [Fact(DisplayName = "Estimate — priced item with no quantity set: contributes to High only, produces a range")]
    public async Task Estimate_NoQuantity_ContributesToHighOnly()
    {
        var list = ShoppingList.Create(Household, Clock);
        // AddItem always requires a quantity for its first contribution — clear it via EditItemQuantity
        // (the qty/unit editor's clear path, plantry-dem) to reach the "quantity unspecified" state.
        var item = list.AddItem(ProductA, quantity: 1m, unitId: UnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);
        list.EditItemQuantity(item.Id, quantity: null, unitId: UnitId, Clock);

        var prices = new FakeShoppingPriceReader();
        prices.RegisterPrice(ProductA, new ShoppingPriceEstimate(ProductA, Price: 5.00m, Quantity: 1m, UnitId: UnitId));

        var svc = BuildService(prices);
        var estimate = await svc.EstimateAsync(list.Items);

        Assert.True(estimate.HasEstimate);
        Assert.True(estimate.IsRange);
        Assert.Equal(0m, estimate.Low);
        Assert.Equal(5.00m, estimate.High); // one pack, at least
    }

    [Fact(DisplayName = "Estimate — priced item with no conversion path onto its unit: uncertain, High only")]
    public async Task Estimate_NoConversionPath_ContributesToHighOnly()
    {
        var list = ShoppingList.Create(Household, Clock);
        list.AddItem(ProductA, quantity: 2m, unitId: OtherUnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);

        var prices = new FakeShoppingPriceReader();
        prices.RegisterPrice(ProductA, new ShoppingPriceEstimate(ProductA, Price: 3.50m, Quantity: 1m, UnitId: UnitId));
        // No RegisterConversion call — TryConvertAsync returns null (no path, cross-dimension).

        var svc = BuildService(prices);
        var estimate = await svc.EstimateAsync(list.Items);

        Assert.True(estimate.IsRange);
        Assert.Equal(0m, estimate.Low);
        Assert.Equal(3.50m, estimate.High);
    }

    // ── No price history (footnoted, never guessed) ───────────────────────────

    [Fact(DisplayName = "Estimate — free-text item: always unpriced (no ProductId to price against)")]
    public async Task Estimate_FreeTextItem_AlwaysUnpriced()
    {
        var list = ShoppingList.Create(Household, Clock);
        list.AddFreeTextItem("Sourdough", quantity: 1m, unitId: null, note: null, Clock);

        var svc = BuildService(new FakeShoppingPriceReader());
        var estimate = await svc.EstimateAsync(list.Items);

        Assert.False(estimate.HasEstimate);
        Assert.Equal(1, estimate.UnpricedCount);
    }

    [Fact(DisplayName = "Estimate — product-backed item with no price observation: unpriced, contributes nothing")]
    public async Task Estimate_ProductWithNoObservation_Unpriced()
    {
        var list = ShoppingList.Create(Household, Clock);
        list.AddItem(ProductA, quantity: 1m, unitId: UnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);

        var svc = BuildService(new FakeShoppingPriceReader()); // no prices registered
        var estimate = await svc.EstimateAsync(list.Items);

        Assert.False(estimate.HasEstimate);
        Assert.Null(estimate.Low);
        Assert.Null(estimate.High);
        Assert.Equal(1, estimate.UnpricedCount);
    }

    [Fact(DisplayName = "Estimate — empty item list: unpriced with a zero footnote count")]
    public async Task Estimate_EmptyList_UnpricedZeroCount()
    {
        var svc = BuildService(new FakeShoppingPriceReader());
        var estimate = await svc.EstimateAsync([]);

        Assert.False(estimate.HasEstimate);
        Assert.Equal(0, estimate.UnpricedCount);
    }

    // ── Mixed aggregation ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Estimate — mixed confident + uncertain + unpriced items aggregate correctly")]
    public async Task Estimate_MixedItems_AggregatesAllThreeBuckets()
    {
        var list = ShoppingList.Create(Household, Clock);
        // Confident: exact line cost.
        list.AddItem(ProductA, quantity: 2m, unitId: UnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);
        // Uncertain: priced but no conversion path.
        list.AddItem(ProductB, quantity: 1m, unitId: OtherUnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);
        // Unpriced: free-text, never priceable.
        list.AddFreeTextItem("Napkins", quantity: 1m, unitId: null, note: null, Clock);

        var prices = new FakeShoppingPriceReader();
        prices.RegisterPrice(ProductA, new ShoppingPriceEstimate(ProductA, Price: 3.00m, Quantity: 1m, UnitId: UnitId)); // $3/unit × 2 = $6
        prices.RegisterPrice(ProductB, new ShoppingPriceEstimate(ProductB, Price: 4.25m, Quantity: 1m, UnitId: UnitId)); // no conversion → High only

        var svc = BuildService(prices);
        var estimate = await svc.EstimateAsync(list.Items);

        Assert.True(estimate.IsRange);
        Assert.Equal(6.00m, estimate.Low);
        Assert.Equal(6.00m + 4.25m, estimate.High);
        Assert.Equal(1, estimate.UnpricedCount);
    }

    [Fact(DisplayName = "Estimate — degenerate zero-quantity observation is treated as unpriced, never divides by zero")]
    public async Task Estimate_DegenerateZeroQuantityObservation_TreatedAsUnpriced()
    {
        var list = ShoppingList.Create(Household, Clock);
        list.AddItem(ProductA, quantity: 1m, unitId: UnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);

        var prices = new FakeShoppingPriceReader();
        prices.RegisterPrice(ProductA, new ShoppingPriceEstimate(ProductA, Price: 5.00m, Quantity: 0m, UnitId: UnitId));

        var svc = BuildService(prices);
        var estimate = await svc.EstimateAsync(list.Items);

        Assert.False(estimate.HasEstimate);
        Assert.Equal(1, estimate.UnpricedCount);
    }

    [Fact(DisplayName = "Estimate — clock's UtcNow date is passed through to the price reader as 'today'")]
    public async Task Estimate_PassesClockDate_ToThePriceReader()
    {
        var list = ShoppingList.Create(Household, Clock);
        list.AddItem(ProductA, quantity: 1m, unitId: UnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);

        var prices = new FakeShoppingPriceReader();
        var fixedClock = new StubClock(new DateTimeOffset(2026, 7, 4, 9, 30, 0, TimeSpan.Zero));

        var svc = BuildService(prices, clock: fixedClock);
        await svc.EstimateAsync(list.Items);

        Assert.Equal(new DateOnly(2026, 7, 4), prices.LastToday);
    }
}
