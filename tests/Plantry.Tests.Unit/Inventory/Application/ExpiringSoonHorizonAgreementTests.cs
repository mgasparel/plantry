using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using RecipesStock = Plantry.Recipes.Application.ProductStock;
using RecipesCatalog = Plantry.Recipes.Application.CatalogProduct;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// The headline guarantee of plantry-5yhd: for the same household data and the same configured
/// "expiring soon" horizon, Inventory's Today expiring-soon widget
/// (<see cref="InventoryQueryService.ExpiringSoonAsync"/>) and the Recipes browse "use soon" filter
/// (<see cref="FulfillmentService"/> → <c>ExpiresWithinDays</c> → <c>HasIngredientExpiringSoon</c>)
/// flag <b>exactly the same set of products</b>. Both classify a lot as expiring-soon with the
/// identical rule "soonest expiry within H days of today", where H is the single per-household
/// setting. This test pins that agreement at, below, and above the boundary so the two surfaces can
/// never drift apart the way the old 7-vs-4 constants did.
/// </summary>
public sealed class ExpiringSoonHorizonAgreementTests
{
    private const int Horizon = 5;
    private static readonly DateOnly Today = new(2026, 6, 14);
    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero));
    private static readonly HouseholdId Household = HouseholdId.New();

    private readonly Guid _grams = Guid.CreateVersion7();
    private readonly Guid _location = Guid.CreateVersion7();
    private readonly Guid _user = Guid.CreateVersion7();

    // Three products at expiry offsets around the horizon: below, at, and beyond.
    private readonly Guid _below = Guid.CreateVersion7();   // today + (Horizon - 1) → in window
    private readonly Guid _boundary = Guid.CreateVersion7(); // today + Horizon      → in window
    private readonly Guid _beyond = Guid.CreateVersion7();  // today + (Horizon + 1) → out of window

    [Fact(DisplayName = "Widget expiring-soon set equals recipe use-soon set for the same data and horizon")]
    public async Task WidgetSet_Equals_RecipeUseSoonSet()
    {
        var offsets = new (Guid Product, int Offset)[]
        {
            (_below, Horizon - 1),
            (_boundary, Horizon),
            (_beyond, Horizon + 1),
        };

        // ── Inventory widget side ───────────────────────────────────────────────
        var widgetProductIds = await WidgetExpiringSoonProductIds(offsets);

        // ── Recipes "use soon" side ─────────────────────────────────────────────
        var recipeUseSoonProductIds = RecipeUseSoonProductIds(offsets);

        // Both surfaces flag exactly the below- and at-boundary products, and never the beyond one.
        var expected = new HashSet<Guid> { _below, _boundary };
        Assert.Equal(expected, widgetProductIds);
        Assert.Equal(expected, recipeUseSoonProductIds);
        Assert.DoesNotContain(_beyond, widgetProductIds);
        Assert.DoesNotContain(_beyond, recipeUseSoonProductIds);
    }

    private async Task<HashSet<Guid>> WidgetExpiringSoonProductIds((Guid Product, int Offset)[] offsets)
    {
        var stocks = new FakeProductStockRepository();
        var catalog = new FakeCatalogReadFacade();
        catalog.UnitCodes[_grams] = "g";
        catalog.LocationNames[_location] = "Pantry";

        foreach (var (product, offset) in offsets)
        {
            catalog.Products.Add(new CatalogProductInfo(product, $"P{offset}", "Pantry", _grams, "g", CanHoldStock: true));
            var stock = ProductStock.Start(Household, product, Clock);
            stock.AddStock(100m, _grams, _location, _user, Clock, expiryDate: Today.AddDays(offset));
            stocks.Items.Add(stock);
        }

        var service = new InventoryQueryService(
            stocks, catalog, new FakeConversionProvider(new IdentityQuantityConverter()),
            new FakeExpiringSoonHorizon(Horizon), Clock, new FakeTenantContext(Household.Value));

        var widget = await service.ExpiringSoonAsync();
        return widget.Select(i => i.ProductId).ToHashSet();
    }

    private HashSet<Guid> RecipeUseSoonProductIds((Guid Product, int Offset)[] offsets)
    {
        var unit = _grams;

        var catalogById = offsets.ToDictionary(
            o => o.Product,
            o => new RecipesCatalog(o.Product, $"P{o.Offset}", TrackStock: true, unit, null, false, []));

        var stockById = offsets.ToDictionary(
            o => o.Product,
            o => new RecipesStock(o.Product, 100m, unit, Today.AddDays(o.Offset)));

        // One recipe with an ingredient per product, in offset order.
        var recipe = Recipe.Create(Household, "Agreement Recipe", 1, Clock).Value;
        recipe.ReplaceIngredients(
            offsets.Select((o, i) => new IngredientLine(o.Product, 10m, unit, null, i)).ToList(),
            Clock);

        var svc = new FulfillmentService(null!, null!, null!, null!); // ports unused by the pure overload
        var result = svc.Compute(recipe, 1, Today, catalogById, stockById, IdentityConverter, Horizon);

        // Map each flagged line back to its product via ingredient ordinal → same order we added them.
        var flagged = new HashSet<Guid>();
        for (var i = 0; i < offsets.Length; i++)
        {
            if (result.Lines[i].ExpiresWithinDays.HasValue)
                flagged.Add(offsets[i].Product);
        }
        return flagged;
    }

    private static Result<decimal> IdentityConverter(Guid _, decimal amount, Guid from, Guid to) =>
        from == to
            ? Result<decimal>.Success(amount)
            : Result<decimal>.Failure(Error.Custom("Test.NoPath", "No conversion path."));

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
