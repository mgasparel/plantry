using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Market.Infrastructure;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.MealPlanning;
using Xunit;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests for <see cref="MealPlanWeekReadModel"/> (ADR-021, plantry-nz3u.1).
///
/// Proves that the cross-schema read model:
/// <list type="bullet">
///   <item>Executes its queries against the real migrated schema without error (contract: column names are stable).</item>
///   <item>Returns the same recipe, ingredient, product, stock, price, unit and conversion data
///     that the individual context ports would have returned.</item>
///   <item>Handles edge cases: no recipes, no stock, no prices.</item>
/// </list>
///
/// RLS isolation (two-household leakage test) belongs to plantry-nz3u.4 — not this suite.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanWeekReadModelTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;

    // Shared unit ids for the test household
    private Guid _gramsId;
    private Guid _kgId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed two units (grams + kilograms) used across all tests.
        await using var catalog = NewCatalogDb(_household);
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var kg = CatalogUnit.Create(_household, "kg", "kilograms", Dimension.Mass, 1000m);
        await catalog.Units.AddRangeAsync(grams, kg);
        await catalog.SaveChangesAsync();

        _gramsId = grams.Id.Value;
        _kgId = kg.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── contract: schema column names are stable ─────────────────────────────────────────────────

    [Fact(DisplayName = "Contract: LoadAsync executes without error on a migrated schema (no data)")]
    public async Task Contract_LoadAsync_ExecutesOnMigratedSchema_WhenNoData()
    {
        var rm = NewReadModel(_household);

        // Empty week — no recipe ids, no product ids.
        // This test fails in CI when any column referenced in the SQL is renamed/dropped.
        var bag = await rm.LoadAsync([], []);

        Assert.NotNull(bag);
        Assert.Empty(bag.Recipes);
        Assert.Empty(bag.IngredientsByRecipe);
        Assert.Empty(bag.Products);
        Assert.Empty(bag.ConversionsByProduct);
        Assert.Empty(bag.StockByProduct);
        Assert.Empty(bag.LatestPriceByProduct);
        // Units are loaded regardless (all household units) — units table was seeded.
        Assert.NotEmpty(bag.Units);
    }

    // ── recipe + ingredient loading ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync returns recipe name and default servings from recipes schema")]
    public async Task LoadAsync_Returns_RecipeFact_From_RecipesSchema()
    {
        // Seed a recipe with two ingredients.
        var productId = await SeedProductAsync("Flour", _gramsId);
        var productId2 = await SeedProductAsync("Sugar", _gramsId);
        var recipeId = await SeedRecipeAsync("Cake", 4,
            (productId, 200m, _gramsId, 1),
            (productId2, 100m, _gramsId, 2));

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([recipeId], []);

        Assert.True(bag.Recipes.ContainsKey(recipeId));
        var recipe = bag.Recipes[recipeId];
        Assert.Equal("Cake", recipe.Name);
        Assert.Equal(4, recipe.DefaultServings);
        // plantry-tyvg: no recipe_photo row seeded — the EXISTS subquery's false branch.
        Assert.False(recipe.HasPhoto);
    }

    /// <summary>
    /// plantry-tyvg: pins the true branch of the <c>EXISTS (SELECT 1 FROM recipes.recipe_photo ...)</c>
    /// subquery in LoadRecipesAsync against the real migrated schema — a recipe with a stored photo
    /// row must report HasPhoto=true. Without this, an always-false or mis-correlated subquery would
    /// pass every other test (the Web-layer tests only exercise the VM→markup path via a fake read model).
    /// </summary>
    [Fact(DisplayName = "LoadAsync returns HasPhoto=true when a recipe_photo row exists (plantry-tyvg)")]
    public async Task LoadAsync_Returns_HasPhotoTrue_WhenPhotoRowExists()
    {
        var recipeId = await SeedRecipeAsync("Photographed Cake", 4);
        await SeedRecipePhotoAsync(recipeId);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([recipeId], []);

        Assert.True(bag.Recipes.ContainsKey(recipeId));
        Assert.True(bag.Recipes[recipeId].HasPhoto);
    }

    [Fact(DisplayName = "LoadAsync returns ingredients for a recipe in ordinal order")]
    public async Task LoadAsync_Returns_Ingredients_InOrdinalOrder()
    {
        var productId1 = await SeedProductAsync("Flour2", _gramsId);
        var productId2 = await SeedProductAsync("Butter", _kgId);
        var recipeId = await SeedRecipeAsync("Bread", 2,
            (productId1, 300m, _gramsId, 1),
            (productId2, 0.1m, _kgId, 2));

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([recipeId], []);

        var ingredients = bag.GetIngredients(recipeId);
        Assert.Equal(2, ingredients.Count);
        Assert.Equal(1, ingredients[0].Ordinal);
        Assert.Equal(productId1, ingredients[0].ProductId);
        Assert.Equal(300m, ingredients[0].Quantity);
        Assert.Equal(_gramsId, ingredients[0].UnitId);
        Assert.Equal(2, ingredients[1].Ordinal);
        Assert.Equal(productId2, ingredients[1].ProductId);
    }

    [Fact(DisplayName = "LoadAsync returns product facts from catalog schema")]
    public async Task LoadAsync_Returns_ProductFacts_From_CatalogSchema()
    {
        var productId = await SeedProductAsync("Rice", _gramsId, trackStock: true);
        var recipeId = await SeedRecipeAsync("Rice dish", 2, (productId, 200m, _gramsId, 1));

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([recipeId], []);

        Assert.True(bag.Products.ContainsKey(productId));
        var product = bag.Products[productId];
        Assert.Equal("Rice", product.Name);
        Assert.True(product.TrackStock);
        Assert.Equal(_gramsId, product.DefaultUnitId);
    }

    // ── stock loading ────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync returns aggregated stock for tracked products")]
    public async Task LoadAsync_Returns_AggregatedStock_ForTrackedProducts()
    {
        var productId = await SeedProductAsync("Pasta", _gramsId, trackStock: true);
        var locationId = await SeedLocationAsync("Pantry");
        // Seed two active stock lots.
        await SeedStockEntryAsync(productId, locationId, 500m, _gramsId, expiryDate: null);
        await SeedStockEntryAsync(productId, locationId, 300m, _gramsId, expiryDate: new DateOnly(2026, 12, 31));

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        Assert.True(bag.StockByProduct.ContainsKey(productId));
        var stock = bag.StockByProduct[productId];
        Assert.True(stock.HasStock);
        // Both lots are in grams — total quantity should sum to 800g across the lots.
        var gramsLot = stock.Lots.FirstOrDefault(l => l.UnitId == _gramsId);
        Assert.NotNull(gramsLot);
        Assert.Equal(800m, gramsLot.TotalQuantity);
    }

    [Fact(DisplayName = "LoadAsync returns soonest expiry across stock lots")]
    public async Task LoadAsync_Returns_SoonestExpiry_AcrossLots()
    {
        var productId = await SeedProductAsync("Milk", _gramsId, trackStock: true);
        var locationId = await SeedLocationAsync("Fridge");
        var sooner = new DateOnly(2026, 7, 1);
        var later = new DateOnly(2026, 7, 10);
        await SeedStockEntryAsync(productId, locationId, 1000m, _gramsId, expiryDate: later);
        await SeedStockEntryAsync(productId, locationId, 500m, _gramsId, expiryDate: sooner);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var stock = bag.GetStock(productId);
        Assert.NotNull(stock);
        Assert.Equal(sooner, stock.SoonestExpiry);
    }

    [Fact(DisplayName = "LoadAsync does not include depleted stock lots")]
    public async Task LoadAsync_ExcludesDepleted_StockLots()
    {
        var productId = await SeedProductAsync("Eggs", _gramsId, trackStock: true);
        var locationId = await SeedLocationAsync("Fridge2");
        // One depleted lot, one active lot.
        await SeedStockEntryAsync(productId, locationId, 200m, _gramsId, expiryDate: null, depleted: true);
        await SeedStockEntryAsync(productId, locationId, 100m, _gramsId, expiryDate: null, depleted: false);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var stock = bag.GetStock(productId);
        Assert.NotNull(stock);
        var gramsLot = stock.Lots.FirstOrDefault(l => l.UnitId == _gramsId);
        Assert.NotNull(gramsLot);
        // Only the active lot (100g) counts — depleted lot excluded.
        Assert.Equal(100m, gramsLot.TotalQuantity);
    }

    [Fact(DisplayName = "LoadAsync does not include non-depleted zero-quantity stock lots")]
    public async Task LoadAsync_ExcludesZeroQuantity_StockLots()
    {
        var productId = await SeedProductAsync("Salt", _gramsId, trackStock: true);
        var locationId = await SeedLocationAsync("Cupboard3");
        // One active lot with zero quantity (not depleted but empty), one with real quantity.
        await SeedStockEntryAsync(productId, locationId, 0m, _gramsId, expiryDate: new DateOnly(2025, 1, 1), depleted: false);
        await SeedStockEntryAsync(productId, locationId, 50m, _gramsId, expiryDate: null, depleted: false);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var stock = bag.GetStock(productId);
        Assert.NotNull(stock);
        var gramsLot = stock.Lots.FirstOrDefault(l => l.UnitId == _gramsId);
        Assert.NotNull(gramsLot);
        // Only the non-zero lot (50g) counts — zero-qty lot excluded.
        Assert.Equal(50m, gramsLot.TotalQuantity);
        // SoonestExpiry from the zero-qty lot must NOT pollute the result.
        Assert.Null(stock.SoonestExpiry);
    }

    // ── price loading ────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync returns latest price per product from pricing schema")]
    public async Task LoadAsync_Returns_LatestPrice_FromPricingSchema()
    {
        var productId = await SeedProductAsync("Oats", _gramsId);
        var older = DateTime.UtcNow.AddDays(-7);
        var newer = DateTime.UtcNow.AddDays(-1);
        await SeedPriceObservationAsync(productId, 2.50m, 500m, _gramsId, unitPrice: 0.005m, observedAt: older);
        await SeedPriceObservationAsync(productId, 3.00m, 500m, _gramsId, unitPrice: 0.006m, observedAt: newer);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var price = bag.GetLatestPrice(productId);
        Assert.NotNull(price);
        // Should return the newer observation (3.00, not 2.50).
        Assert.Equal(3.00m, price.Price);
        Assert.Equal(0.006m, price.UnitPrice);
    }

    [Fact(DisplayName = "LoadAsync returns null price when no price history exists")]
    public async Task LoadAsync_Returns_NullPrice_WhenNoPriceHistory()
    {
        var productId = await SeedProductAsync("Vinegar", _gramsId);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        Assert.Null(bag.GetLatestPrice(productId));
    }

    [Fact(DisplayName = "LoadAsync never returns a superseded observation (ADR-023 A7) — even with the SAME observed_at as its amendment")]
    public async Task LoadAsync_Excludes_Superseded_Price_Even_With_Same_ObservedAt()
    {
        // The exact DISTINCT ON hazard: an amending row copies the original's observed_at verbatim
        // (A7 — "the price event's time didn't change"), so a naive `ORDER BY observed_at DESC` with no
        // superseded filter can tie-break onto the dead row. Same observed_at pins that failure mode
        // deterministically rather than relying on the amendment merely being newer.
        var productId = await SeedProductAsync("Onions", _gramsId);
        var observedAt = DateTime.UtcNow.AddDays(-1);
        var originalId = await SeedPriceObservationAsync(
            productId, price: 3.98m, quantity: 1000m, _gramsId, unitPrice: 0.00398m, observedAt);
        var amendmentId = await SeedPriceObservationAsync(
            productId, price: 3.98m, quantity: 3000m, _gramsId, unitPrice: 0.001327m, observedAt);
        await SupersedeAsync(originalId, amendmentId);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var price = bag.GetLatestPrice(productId);
        Assert.NotNull(price);
        Assert.Equal(3000m, price.Quantity); // the live amendment, never the superseded original
        Assert.Equal(0.001327m, price.UnitPrice);
    }

    // ── deal-aware price loading (plantry-epzj: parity with PricingQueries.EffectivePriceAsync;
    //    plantry-pxjp: except an unusable-unit Deal, where this read model instead parities with
    //    PricingQueries.EffectiveCostablePriceAsync) ──────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync: a live in-window Deal wins over a Purchase, cheapest by unit_price")]
    public async Task LoadAsync_Deal_Wins_When_Live_And_Cheaper_Than_Purchase()
    {
        var productId = await SeedProductAsync("Cereal", _gramsId);
        var today = new DateOnly(2026, 7, 15);

        await SeedPriceObservationAsync(
            productId, price: 4m, quantity: 500m, _gramsId, unitPrice: 0.008m, DateTime.UtcNow.AddDays(-1));
        await SeedDealObservationAsync(
            productId, price: 3m, quantity: 500m, _gramsId, unitPrice: 0.006m,
            validFrom: today.AddDays(-2), validTo: today.AddDays(2), observedAt: DateTime.UtcNow);

        var rm = NewReadModel(_household, FixedClockAt(today));
        var bag = await rm.LoadAsync([], [productId]);

        var price = bag.GetLatestPrice(productId);
        Assert.NotNull(price);
        Assert.Equal(3m, price.Price); // the cheaper live deal, not the purchase
        Assert.Equal(0.006m, price.UnitPrice);

        await AssertParityAsync(productId, today);
    }

    [Fact(DisplayName = "LoadAsync: an expired Deal never wins even with a later observed_at than the Purchase")]
    public async Task LoadAsync_Purchase_Wins_When_Deal_Expired_Despite_Later_ObservedAt()
    {
        var productId = await SeedProductAsync("Pasta2", _gramsId);
        var today = new DateOnly(2026, 7, 15);

        await SeedPriceObservationAsync(
            productId, price: 4m, quantity: 500m, _gramsId, unitPrice: 0.008m, DateTime.UtcNow.AddDays(-5));
        // Observed more recently than the purchase, but its validity window lapsed before "today" —
        // must never surface regardless of raw observed_at ordering.
        await SeedDealObservationAsync(
            productId, price: 2m, quantity: 500m, _gramsId, unitPrice: 0.004m,
            validFrom: today.AddDays(-10), validTo: today.AddDays(-3), observedAt: DateTime.UtcNow.AddDays(-1));

        var rm = NewReadModel(_household, FixedClockAt(today));
        var bag = await rm.LoadAsync([], [productId]);

        var price = bag.GetLatestPrice(productId);
        Assert.NotNull(price);
        Assert.Equal(4m, price.Price); // purchase wins; expired deal never surfaces

        await AssertParityAsync(productId, today);
    }

    [Fact(DisplayName = "LoadAsync: a superseded Deal never wins even if its window would otherwise be live")]
    public async Task LoadAsync_Purchase_Wins_When_Deal_Superseded()
    {
        var productId = await SeedProductAsync("Butter2", _gramsId);
        var today = new DateOnly(2026, 7, 15);

        var purchaseId = await SeedPriceObservationAsync(
            productId, price: 5m, quantity: 500m, _gramsId, unitPrice: 0.010m, DateTime.UtcNow.AddDays(-3));
        // Live, cheap deal — would win the leg-2 query outright if not for the supersede below.
        var dealId = await SeedDealObservationAsync(
            productId, price: 1m, quantity: 500m, _gramsId, unitPrice: 0.002m,
            validFrom: today.AddDays(-2), validTo: today.AddDays(2), observedAt: DateTime.UtcNow);
        // Mark the deal superseded (ADR-023 A7). The FK only requires a real existing row id — reusing
        // the purchase's own id here (as this file's existing SupersedeAsync raw-SQL helper does
        // elsewhere) keeps the seed minimal; the test targets the superseded-filter behaviour, not a
        // realistic amendment chain.
        await SupersedeAsync(dealId, purchaseId);

        var rm = NewReadModel(_household, FixedClockAt(today));
        var bag = await rm.LoadAsync([], [productId]);

        var price = bag.GetLatestPrice(productId);
        Assert.NotNull(price);
        Assert.Equal(5m, price.Price); // purchase wins; superseded deal must never surface

        await AssertParityAsync(productId, today);
    }

    [Fact(DisplayName = "LoadAsync: a deal-only product whose window lapsed yields no price fact")]
    public async Task LoadAsync_Returns_NullPrice_WhenOnlyDeal_HasLapsed()
    {
        var productId = await SeedProductAsync("Yeast", _gramsId);
        var today = new DateOnly(2026, 7, 15);

        // No Purchase/Manual observation at all — only a Deal, and its window has lapsed.
        await SeedDealObservationAsync(
            productId, price: 1m, quantity: 500m, _gramsId, unitPrice: 0.002m,
            validFrom: today.AddDays(-10), validTo: today.AddDays(-3), observedAt: DateTime.UtcNow.AddDays(-3));

        var rm = NewReadModel(_household, FixedClockAt(today));
        var bag = await rm.LoadAsync([], [productId]);

        Assert.Null(bag.GetLatestPrice(productId));

        await AssertParityAsync(productId, today);
    }

    [Fact(DisplayName = "plantry-pxjp: LoadAsync skips an active unitless Deal (DM-17) and falls back to the costable Purchase")]
    public async Task LoadAsync_Purchase_Wins_When_Active_Deal_Is_Unitless()
    {
        var productId = await SeedProductAsync("Broccoli", _gramsId);
        var today = new DateOnly(2026, 7, 15);

        await SeedPriceObservationAsync(
            productId, price: 12m, quantity: 4m, _gramsId, unitPrice: 3m, DateTime.UtcNow.AddDays(-1));
        // A deal confirmed without a pack size (DM-17): empty unit, null unit price. Active and
        // cheaper by raw price alone — must never shadow the costable purchase above.
        await SeedDealObservationAsync(
            productId, price: 2.49m, quantity: 1m, Guid.Empty, unitPrice: null,
            validFrom: today.AddDays(-2), validTo: today.AddDays(2), observedAt: DateTime.UtcNow);

        var rm = NewReadModel(_household, FixedClockAt(today));
        var bag = await rm.LoadAsync([], [productId]);

        var price = bag.GetLatestPrice(productId);
        Assert.NotNull(price);
        Assert.Equal(12m, price.Price); // the costable purchase, never the unitless deal
        Assert.Equal(3m, price.UnitPrice);

        // Deliberately does NOT call AssertParityAsync here: PricingQueries.EffectivePriceAsync (the
        // display/sales-callout read) still surfaces the unitless deal by design (plantry-pxjp) — this
        // batched read model instead parities with EffectiveCostablePriceAsync for costing callers.
        await using var pricingDb = NewPricingDb();
        var queries = new PricingQueries(new PriceObservationRepository(pricingDb));
        var costableExpected = await queries.EffectiveCostablePriceAsync(productId, today);
        Assert.NotNull(costableExpected);
        Assert.Equal(costableExpected.Price, price.Price);
        Assert.Equal(costableExpected.UnitId, price.UnitId);
        Assert.Equal(costableExpected.UnitPrice, price.UnitPrice);
    }

    /// <summary>
    /// Pins parity between the batched read model and <see cref="PricingQueries.EffectivePriceAsync"/>
    /// for the given product/today, using the same underlying data (acceptance criterion: "matching
    /// EffectivePriceAsync for the same data").
    /// </summary>
    private async Task AssertParityAsync(Guid productId, DateOnly today)
    {
        await using var pricingDb = NewPricingDb();
        var queries = new PricingQueries(new PriceObservationRepository(pricingDb));
        var expected = await queries.EffectivePriceAsync(productId, today);

        var rm = NewReadModel(_household, FixedClockAt(today));
        var bag = await rm.LoadAsync([], [productId]);
        var actual = bag.GetLatestPrice(productId);

        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Price, actual.Price);
        Assert.Equal(expected.Quantity, actual.Quantity);
        Assert.Equal(expected.UnitId, actual.UnitId);
        Assert.Equal(expected.UnitPrice, actual.UnitPrice);
    }

    // ── unit and conversion loading ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync returns all household units from catalog schema")]
    public async Task LoadAsync_Returns_AllUnits_FromCatalogSchema()
    {
        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], []);

        // Both seeded units (g and kg) should be present.
        Assert.Contains(_gramsId, bag.Units.Keys);
        Assert.Contains(_kgId, bag.Units.Keys);
        Assert.Equal("g", bag.Units[_gramsId].Code);
        Assert.Equal("kg", bag.Units[_kgId].Code);
    }

    [Fact(DisplayName = "LoadAsync returns product conversions from catalog schema")]
    public async Task LoadAsync_Returns_ProductConversions_FromCatalogSchema()
    {
        var productId = await SeedProductAsync("Honey", _gramsId);
        // Add a conversion: 1 kg honey = 1350 g (density).
        await SeedConversionAsync(productId, _kgId, _gramsId, 1350m);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var conversions = bag.GetConversions(productId);
        Assert.Single(conversions);
        Assert.Equal(_kgId, conversions[0].FromUnitId);
        Assert.Equal(_gramsId, conversions[0].ToUnitId);
        Assert.Equal(1350m, conversions[0].Factor);
    }

    // ── cross-schema: ingredient product ids gathered automatically ──────────────────────────────

    [Fact(DisplayName = "LoadAsync gathers product ids from ingredient list automatically")]
    public async Task LoadAsync_GathersIngredientProductIds_Automatically()
    {
        // Recipe with an ingredient — caller only passes the recipe id, not the product id.
        // The read model must gather the ingredient's product id and load its product fact.
        var productId = await SeedProductAsync("Salt", _gramsId);
        var recipeId = await SeedRecipeAsync("Salted water", 2, (productId, 5m, _gramsId, 1));

        var rm = NewReadModel(_household);
        // Pass only the recipeId, NOT the productId explicitly.
        var bag = await rm.LoadAsync([recipeId], []);

        Assert.True(bag.Products.ContainsKey(productId),
            "Product referenced by recipe ingredient should be loaded without explicit caller seeding.");
    }

    // ── inclusion closure (plantry-yqse) ────────────────────────────────────────────────────────

    [Fact(DisplayName = "plantry-yqse: LoadAsync widens the loaded set to a sub-recipe when the requested recipe includes it")]
    public async Task LoadAsync_Widens_ToSubRecipe_WhenRequestedRecipe_Includes_It()
    {
        var yogurtId = await SeedProductAsync("Yogurt", _gramsId);
        var subId = await SeedRecipeAsync("Tzatziki", 4, (yogurtId, 400m, _gramsId, 1));

        var pitaId = await SeedProductAsync("Pita", _gramsId);
        var parentId = await SeedRecipeAsync("Gyro Wrap", 2, (pitaId, 200m, _gramsId, 1));
        await SeedInclusionAsync(parentId, subId, servings: 2m, groupHeading: null, ordinal: 1);

        var rm = NewReadModel(_household);
        // Only the PARENT id is requested — the sub is discovered purely via the inclusion closure.
        var bag = await rm.LoadAsync([parentId], []);

        Assert.True(bag.Recipes.ContainsKey(subId), "The sub-recipe must be loaded via the inclusion closure.");
        Assert.Equal("Tzatziki", bag.Recipes[subId].Name);
        var subIngredients = bag.GetIngredients(subId);
        var line = Assert.Single(subIngredients);
        Assert.Equal(yogurtId, line.ProductId);
        // The sub's own ingredient product must be loaded too (catalog schema), without explicit seeding.
        Assert.True(bag.Products.ContainsKey(yogurtId));
    }

    [Fact(DisplayName = "plantry-yqse: LoadAsync follows a transitive (3-level) inclusion chain")]
    public async Task LoadAsync_Follows_TransitiveInclusionChain()
    {
        var garlicId = await SeedProductAsync("Garlic", _gramsId);
        var leafId = await SeedRecipeAsync("Garlic Paste", 1, (garlicId, 10m, _gramsId, 1));

        var yogurtId = await SeedProductAsync("Yogurt2", _gramsId);
        var midId = await SeedRecipeAsync("Tzatziki2", 4, (yogurtId, 400m, _gramsId, 1));
        await SeedInclusionAsync(midId, leafId, servings: 1m, groupHeading: null, ordinal: 2);

        var pitaId = await SeedProductAsync("Pita2", _gramsId);
        var rootId = await SeedRecipeAsync("Gyro Wrap2", 2, (pitaId, 200m, _gramsId, 1));
        await SeedInclusionAsync(rootId, midId, servings: 2m, groupHeading: null, ordinal: 1);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([rootId], []);

        Assert.True(bag.Recipes.ContainsKey(midId), "The direct sub must load.");
        Assert.True(bag.Recipes.ContainsKey(leafId), "The transitively-included leaf must load too.");
        Assert.True(bag.Products.ContainsKey(garlicId), "The leaf's own ingredient product must load.");
    }

    /// <summary>
    /// Pins the fix for a critic finding on this same ticket (plantry-yqse pass 1): the recursive CTE's
    /// SEED term matches every recipe id in <c>@ids</c> directly, and its RECURSIVE term matches whatever
    /// the seed/prior step discovered — so when BOTH a root and one of its own subs are requested in the
    /// SAME <c>LoadAsync</c> call (exactly what happens on a real week page, where the mid-level recipe
    /// may also be planned standalone in another cell), the mid recipe's inclusion edge is discovered
    /// TWICE: once directly (mid is itself a seed id) and once via the root's recursive walk into mid.
    /// Without the outer <c>SELECT DISTINCT</c> (and the belt-and-braces <c>seenInclusionIds</c> guard),
    /// the edge would be duplicated in <c>inclusionsByRecipe</c>, and <c>RecipeExpansionService</c> would
    /// then walk the leaf twice — silently doubling its cost/fulfillment contribution.
    /// </summary>
    [Fact(DisplayName = "plantry-yqse: LoadAsync does not duplicate an inclusion edge when both a root and its own sub are requested together")]
    public async Task LoadAsync_DoesNotDuplicate_InclusionEdge_WhenRootAndSub_BothRequested()
    {
        var garlicId = await SeedProductAsync("Garlic2", _gramsId);
        var leafId = await SeedRecipeAsync("Garlic Paste2", 1, (garlicId, 10m, _gramsId, 1));

        var yogurtId = await SeedProductAsync("Yogurt4", _gramsId);
        var midId = await SeedRecipeAsync("Tzatziki3", 4, (yogurtId, 400m, _gramsId, 1));
        await SeedInclusionAsync(midId, leafId, servings: 1m, groupHeading: null, ordinal: 2);

        var pitaId = await SeedProductAsync("Pita4", _gramsId);
        var rootId = await SeedRecipeAsync("Gyro Wrap3", 2, (pitaId, 200m, _gramsId, 1));
        await SeedInclusionAsync(rootId, midId, servings: 2m, groupHeading: null, ordinal: 1);

        var rm = NewReadModel(_household);
        // Both the root AND the mid-level sub requested in one call — mirrors a week planning BOTH
        // Gyro Wrap and a standalone Tzatziki in different cells.
        var bag = await rm.LoadAsync([rootId, midId], []);

        var midInclusions = bag.GetInclusions(midId);
        Assert.Single(midInclusions);
        Assert.Equal(leafId, midInclusions[0].SubRecipeId);
    }

    [Fact(DisplayName = "plantry-yqse: WeekBag.GetInclusions returns the owning recipe's inclusion edges, ordinal-ordered")]
    public async Task WeekBag_GetInclusions_Returns_OrdinalOrdered_Edges()
    {
        var yogurtId = await SeedProductAsync("Yogurt3", _gramsId);
        var subAId = await SeedRecipeAsync("Sub A", 2, (yogurtId, 100m, _gramsId, 1));
        var subBId = await SeedRecipeAsync("Sub B", 2, (yogurtId, 100m, _gramsId, 1));

        var pitaId = await SeedProductAsync("Pita3", _gramsId);
        var parentId = await SeedRecipeAsync("Combo Wrap", 2, (pitaId, 200m, _gramsId, 1));
        // Seeded out of ordinal order to prove the read model sorts, not just preserves insert order.
        await SeedInclusionAsync(parentId, subBId, servings: 1m, groupHeading: "Second", ordinal: 2);
        await SeedInclusionAsync(parentId, subAId, servings: 1m, groupHeading: "First", ordinal: 1);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([parentId], []);

        var inclusions = bag.GetInclusions(parentId);
        Assert.Equal(2, inclusions.Count);
        Assert.Equal(subAId, inclusions[0].SubRecipeId);
        Assert.Equal("First", inclusions[0].GroupHeading);
        Assert.Equal(1, inclusions[0].Ordinal);
        Assert.Equal(subBId, inclusions[1].SubRecipeId);
        Assert.Equal("Second", inclusions[1].GroupHeading);
        Assert.Equal(2, inclusions[1].Ordinal);
    }

    [Fact(DisplayName = "plantry-yqse: LoadAsync terminates instead of hanging on a defensively-cyclic inclusion chain")]
    public async Task LoadAsync_Terminates_OnDefensivelyCyclicInclusionChain()
    {
        // N4 (application layer) blocks a cyclic inclusion at save time — this seeds one directly via
        // raw SQL to prove the read model's recursive CTE visited-array guard is a genuine defensive
        // backstop, not merely inherited safety from the write-side invariant.
        var productId = await SeedProductAsync("Cyclic Product", _gramsId);
        var aId = await SeedRecipeAsync("Cycle A", 1, (productId, 1m, _gramsId, 1));
        var bId = await SeedRecipeAsync("Cycle B", 1, (productId, 1m, _gramsId, 1));
        await SeedInclusionAsync(aId, bId, servings: 1m, groupHeading: null, ordinal: 2);
        await SeedInclusionAsync(bId, aId, servings: 1m, groupHeading: null, ordinal: 2);

        var rm = NewReadModel(_household);

        // Must complete (the visited-array guard drops the revisiting edge) rather than hang or throw.
        var bag = await rm.LoadAsync([aId], []).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(bag.Recipes.ContainsKey(aId));
        Assert.True(bag.Recipes.ContainsKey(bId));
    }

    /// <summary>Seeds a row into recipes.recipe_inclusion directly (raw SQL) — mirrors this file's
    /// SeedRecipeAsync convention for the recipe/recipe_ingredient tables.</summary>
    private async Task SeedInclusionAsync(
        Guid recipeId, Guid subRecipeId, decimal servings, string? groupHeading, int ordinal)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recipes.recipe_inclusion
                (inclusion_id, household_id, recipe_id, sub_recipe_id, servings, group_heading, ordinal)
            VALUES
                (@id, @hid, @rid, @sid, @servings, @heading, @ord)
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("rid", recipeId);
        cmd.Parameters.AddWithValue("sid", subRecipeId);
        cmd.Parameters.AddWithValue("servings", servings);
        cmd.Parameters.AddWithValue("heading", (object?)groupHeading ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ord", ordinal);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── O(1) lookup helpers ──────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "WeekBag.GetRecipe returns null for unknown recipe id")]
    public async Task WeekBag_GetRecipe_ReturnsNull_WhenNotLoaded()
    {
        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], []);

        Assert.Null(bag.GetRecipe(Guid.NewGuid()));
    }

    [Fact(DisplayName = "WeekBag.GetStock returns null when product has no active stock")]
    public async Task WeekBag_GetStock_ReturnsNull_WhenNoActiveStock()
    {
        var productId = await SeedProductAsync("Pepper", _gramsId);
        // No stock seeded.

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        Assert.Null(bag.GetStock(productId));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private MealPlanWeekReadModel NewReadModel(HouseholdId household) =>
        NewReadModel(household, Clock);

    private MealPlanWeekReadModel NewReadModel(HouseholdId household, IClock clock)
    {
        var tenant = new TenantContext();
        tenant.Set(household.Value);
        return new MealPlanWeekReadModel(db.ConnectionString, tenant, clock);
    }

    private CatalogDbContext NewCatalogDb(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new CatalogDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private RecipesDbContext NewRecipesDb()
    {
        var opts = new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new RecipesDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private InventoryDbContext NewInventoryDb()
    {
        var opts = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new InventoryDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private MarketDbContext NewPricingDb()
    {
        var opts = new DbContextOptionsBuilder<MarketDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new MarketDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    /// <summary>Seeds a product into catalog.products; returns the product id as Guid.</summary>
    private async Task<Guid> SeedProductAsync(string name, Guid defaultUnitId, bool trackStock = true)
    {
        await using var catalog = NewCatalogDb(_household);
        var unitId = UnitId.From(defaultUnitId);
        var product = Product.Create(_household, name, unitId, Clock, trackStock: trackStock);
        await catalog.Products.AddAsync(product);
        await catalog.SaveChangesAsync();
        return product.Id.Value;
    }

    /// <summary>Seeds a recipe with the given ingredients into recipes.recipe + recipe_ingredient.</summary>
    private async Task<Guid> SeedRecipeAsync(
        string name,
        int defaultServings,
        params (Guid ProductId, decimal Quantity, Guid UnitId, int Ordinal)[] ingredients)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // INSERT into recipes.recipe
        var recipeId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO recipes.recipe
                    (recipe_id, household_id, name, default_servings, created_at, updated_at)
                VALUES
                    (@id, @hid, @name, @servings, NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", recipeId);
            cmd.Parameters.AddWithValue("hid", _household.Value);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("servings", defaultServings);
            await cmd.ExecuteNonQueryAsync();
        }

        // INSERT recipe_ingredient rows
        foreach (var (productId, quantity, unitId, ordinal) in ingredients)
        {
            await using var ingCmd = conn.CreateCommand();
            ingCmd.CommandText = """
                INSERT INTO recipes.recipe_ingredient
                    (ingredient_id, household_id, recipe_id, product_id, quantity, unit_id, ordinal)
                VALUES
                    (@id, @hid, @rid, @pid, @qty, @uid, @ord)
                """;
            ingCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            ingCmd.Parameters.AddWithValue("hid", _household.Value);
            ingCmd.Parameters.AddWithValue("rid", recipeId);
            ingCmd.Parameters.AddWithValue("pid", productId);
            ingCmd.Parameters.AddWithValue("qty", quantity);
            ingCmd.Parameters.AddWithValue("uid", unitId);
            ingCmd.Parameters.AddWithValue("ord", ordinal);
            await ingCmd.ExecuteNonQueryAsync();
        }

        return recipeId;
    }

    /// <summary>
    /// Seeds a row into recipes.recipe_photo for the given recipe (plantry-tyvg) — pins the true
    /// branch of the <c>EXISTS (SELECT 1 FROM recipes.recipe_photo ...)</c> subquery in
    /// LoadRecipesAsync against the real schema. Mirrors the RecipePhoto entity's column set
    /// (RecipesDbContext.cs): recipe_id (PK/FK), household_id, content, content_type, sha256
    /// (nullable), created_at, updated_at.
    /// </summary>
    private async Task SeedRecipePhotoAsync(Guid recipeId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recipes.recipe_photo
                (recipe_id, household_id, content, content_type, created_at, updated_at)
            VALUES
                (@rid, @hid, @content, @ctype, NOW(), NOW())
            """;
        cmd.Parameters.AddWithValue("rid", recipeId);
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("content", new byte[] { 1, 2, 3 });
        cmd.Parameters.AddWithValue("ctype", "image/webp");
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds a location into catalog.locations; returns the location id.</summary>
    private async Task<Guid> SeedLocationAsync(string name)
    {
        await using var catalog = NewCatalogDb(_household);
        var location = Location.Create(_household, name, LocationType.Ambient);
        await catalog.Locations.AddAsync(location);
        await catalog.SaveChangesAsync();
        return location.Id.Value;
    }

    /// <summary>Seeds an inventory stock entry; returns the entry id.</summary>
    private async Task SeedStockEntryAsync(
        Guid productId,
        Guid locationId,
        decimal quantity,
        Guid unitId,
        DateOnly? expiryDate,
        bool depleted = false)
    {
        // Ensure the product_stock root row exists.
        await EnsureProductStockAsync(productId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        var depletedAt = depleted ? (object)DateTime.UtcNow : DBNull.Value;
        var expiryObj = expiryDate.HasValue ? (object)expiryDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

        cmd.CommandText = """
            INSERT INTO inventory.stock_entry
                (entry_id, household_id, product_id, location_id, quantity, unit_id, expiry_date,
                 is_open, created_at, updated_at, depleted_at, purchased_at)
            VALUES
                (@id, @hid, @pid, @lid, @qty, @uid, @exp,
                 false, NOW(), NOW(), @dep, NOW())
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("lid", locationId);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("exp", expiryObj);
        cmd.Parameters.AddWithValue("dep", depletedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureProductStockAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.product_stock (household_id, product_id, created_at, updated_at)
            VALUES (@hid, @pid, NOW(), NOW())
            ON CONFLICT (household_id, product_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds a price observation into pricing.price_observation. Returns its generated id so a
    /// test can chain a <see cref="SupersedeAsync"/> call (ADR-023 A7).</summary>
    private async Task<Guid> SeedPriceObservationAsync(
        Guid productId,
        decimal price,
        decimal quantity,
        Guid unitId,
        decimal? unitPrice,
        DateTime observedAt)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var id = Guid.NewGuid();
        await using var cmd = conn.CreateCommand();
        var unitPriceObj = unitPrice.HasValue ? (object)unitPrice.Value : DBNull.Value;

        cmd.CommandText = """
            INSERT INTO pricing.price_observation
                (observation_id, household_id, product_id, price, quantity, unit_id, unit_price,
                 source, source_ref, observed_at, user_id)
            VALUES
                (@id, @hid, @pid, @price, @qty, @uid, @up,
                 'Purchase', @ref, @obs, @usr)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("up", unitPriceObj);
        cmd.Parameters.AddWithValue("ref", Guid.NewGuid());
        cmd.Parameters.AddWithValue("obs", observedAt);
        cmd.Parameters.AddWithValue("usr", Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Binds <c>superseded_by_id</c> on an existing observation (ADR-023 A7) — the raw-SQL
    /// equivalent of <see cref="Plantry.Market.Domain.PriceObservation.Supersede"/>, used to seed the
    /// dead half of an amendment pair directly against the schema (mirroring this file's raw-SQL seeding
    /// convention rather than pulling in the EF entity).</summary>
    private async Task SupersedeAsync(Guid observationId, Guid replacementId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pricing.price_observation
            SET superseded_by_id = @replacement
            WHERE observation_id = @id
            """;
        cmd.Parameters.AddWithValue("replacement", replacementId);
        cmd.Parameters.AddWithValue("id", observationId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds a Deal-source price observation (with a validity window) into
    /// pricing.price_observation. Returns its generated id so a test can chain a
    /// <see cref="SupersedeAsync"/> call (ADR-023 A7).</summary>
    private async Task<Guid> SeedDealObservationAsync(
        Guid productId,
        decimal price,
        decimal quantity,
        Guid unitId,
        decimal? unitPrice,
        DateOnly validFrom,
        DateOnly validTo,
        DateTime observedAt)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var id = Guid.NewGuid();
        await using var cmd = conn.CreateCommand();
        var unitPriceObj = unitPrice.HasValue ? (object)unitPrice.Value : DBNull.Value;

        cmd.CommandText = """
            INSERT INTO pricing.price_observation
                (observation_id, household_id, product_id, price, quantity, unit_id, unit_price,
                 source, source_ref, observed_at, user_id, valid_from, valid_to)
            VALUES
                (@id, @hid, @pid, @price, @qty, @uid, @up,
                 'Deal', @ref, @obs, @usr, @vf, @vt)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("up", unitPriceObj);
        cmd.Parameters.AddWithValue("ref", Guid.NewGuid());
        cmd.Parameters.AddWithValue("obs", observedAt);
        cmd.Parameters.AddWithValue("usr", Guid.NewGuid());
        cmd.Parameters.AddWithValue("vf", validFrom.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("vt", validTo.ToDateTime(TimeOnly.MinValue));
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>A fixed-"today" <see cref="IClock"/> for deal-window tests, mirroring the pattern in
    /// <c>PurchaseStoreBackfillTests.FixedClock</c> — noon UTC on the given date, so
    /// <c>DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)</c> lands unambiguously on that date.</summary>
    private static IClock FixedClockAt(DateOnly date) =>
        new FixedClock(new DateTimeOffset(date.Year, date.Month, date.Day, 12, 0, 0, TimeSpan.Zero));

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Seeds a product conversion into catalog.product_conversions.</summary>
    private async Task SeedConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor)
    {
        await using var catalog = NewCatalogDb(_household);
        // Use raw SQL to insert the conversion directly (ProductConversion entity doesn't have a
        // public static factory in this project — conversions are managed via Product.AddConversion).
        await catalog.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.product_conversions
                (id, household_id, product_id, from_unit_id, to_unit_id, factor)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5})
            """,
            Guid.NewGuid(), _household.Value, productId, fromUnitId, toUnitId, factor);
    }
}
