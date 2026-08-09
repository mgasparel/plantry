using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests for <see cref="RecipeReadModelAdapter"/>'s J6 enrichment + ShopForWeek shortfall
/// paths reading a recipe's <b>EXPANDED</b> view (recipe-composition.md §7, D4 — plantry-ckzc).
///
/// A parent recipe with NO direct ingredients that includes a sub-recipe is expanded through the real
/// <see cref="RecipeExpansionService"/> over a Postgres-backed <see cref="RecipesDbContext"/>, so a dish
/// that draws its ingredients entirely from an inclusion rolls up the sub's product cost/fulfillment/shortfall
/// — the same expanded figures the Details page (J5) shows. A FLAT computation over the parent's own (empty)
/// ingredient list would report 100% fulfillment, no cost, and no shortfall, so each assertion below fails
/// unless expansion actually drives the roll-up.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeReadModelAdapterExpandedTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private static readonly DateOnly Today = new(2026, 7, 10);
    /// <summary>Fixed at noon UTC on <see cref="Today"/> (missing-seam:iclock-web, plantry-4tb4) — the adapter
    /// now derives its own <c>today</c> for <c>GetMissingIngredientsAsync</c> via <c>clock.ToLocalDate(clock.UtcNow)</c>,
    /// so this must resolve to the same calendar day as the pinned <see cref="Today"/> constant rather than the
    /// real wall clock's <c>TimeZoneInfo.Local</c> zone (gate 10).</summary>
    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));

    // Soft-ref catalog ids (never inserted into Catalog; the fake readers below stand in).
    private readonly Guid _cheeseProductId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── J6 enrichment reflects the included sub-recipe's product ─────────────────

    [Fact(DisplayName = "GetEnrichmentAsync rolls up an included sub-recipe's product (expanded, not flat)")]
    public async Task GetEnrichmentAsync_Reflects_Included_SubRecipe()
    {
        var parentId = await SeedParentIncludingSubAsync();

        await using var ctx = NewContext();
        var adapter = BuildAdapter(ctx,
            // Cheese is tracked but has NO stock → Missing.
            catalog: FakeCatalog.WithTrackedLeaf(_cheeseProductId, _unitId),
            stock: new FakeStock(),                    // no stock rows
            prices: FakePrices.With(_cheeseProductId, 0.01m, _unitId));

        var enrichment = await adapter.GetEnrichmentAsync(parentId.Value, servings: 2, Today);

        Assert.NotNull(enrichment);
        // Expanded: the sub's cheese is the only tracked line and it is Missing → 0% (flat would be 100%,
        // since the parent has no direct ingredients).
        Assert.Equal(0, enrichment!.FulfillmentPercent);
        // Total cost = expanded per-serving cost × servings = ((100 × $0.01) / 2) × 2 = $1.00 (flat → null).
        Assert.Equal(1.00m, enrichment.TotalCost);
        Assert.False(enrichment.CostIsPartial);
    }

    // ── J6 ShopForWeek shortfall reflects the included sub-recipe's product ──────

    [Fact(DisplayName = "GetMissingIngredientsAsync returns the included sub-recipe's shortfall (expanded, not flat)")]
    public async Task GetMissingIngredientsAsync_Reflects_Included_SubRecipe()
    {
        var parentId = await SeedParentIncludingSubAsync();

        await using var ctx = NewContext();
        var adapter = BuildAdapter(ctx,
            catalog: FakeCatalog.WithTrackedLeaf(_cheeseProductId, _unitId),
            stock: new FakeStock(),                    // cheese Missing → full shortfall
            prices: new FakePrices());

        var missing = await adapter.GetMissingIngredientsAsync(parentId.Value, servings: 2);

        // Expanded → one shortfall line for the sub's cheese (flat would be empty: parent has no ingredients).
        var line = Assert.Single(missing);
        Assert.Equal(_cheeseProductId, line.ProductId);
        Assert.Equal(_unitId, line.UnitId);
        // factor = 2 servings ÷ sub DefaultServings 2 = 1; scale = 2 ÷ parent DefaultServings 2 = 1 → 100 × 1 × 1.
        Assert.Equal(100m, line.Quantity);
    }

    // ── J6 shortfall is suppressed/reduced by the substitution closure (plantry-aqpa.4) ──────────
    //
    // Real DI wiring end to end: a real Postgres-backed SubstitutionReader (Plantry.Recipes.Infrastructure)
    // resolves the edge seeded via the real SubstitutionRepository (mirrors SubstitutionTests.cs's
    // seeding pattern), so this exercises the exact production composition
    // (RecipeReadModelAdapter -> FulfillmentService -> ISubstitutionReader) rather than a fake reader.

    [Fact(DisplayName = "GetMissingIngredientsAsync suppresses the shortfall when a real DB-backed substitution edge fully covers the requirement (plantry-aqpa.4)")]
    public async Task GetMissingIngredientsAsync_Suppressed_When_Real_Substitution_Edge_Fully_Covers()
    {
        var unitId = Guid.CreateVersion7();
        var chickpeasCanned = Guid.CreateVersion7();
        var chickpeasDried = Guid.CreateVersion7();

        var recipeId = await SeedFlatRecipeAsync(chickpeasCanned, qty: 100m, unitId);
        await SeedSubstitutionEdgeAsync(chickpeasCanned, unitId, chickpeasDried, unitId);

        await using var ctx = NewContext();
        var adapter = BuildAdapter(ctx,
            catalog: FakeCatalog.WithTrackedLeaf(chickpeasCanned, unitId).AddTrackedLeaf(chickpeasDried, unitId, "Chickpeas (dried)"),
            // Direct stock alone is short (30 of 100), but the substitute covers the remainder.
            stock: new FakeStock().Add(chickpeasCanned, 30m, unitId).Add(chickpeasDried, 70m, unitId),
            prices: new FakePrices(),
            substitutions: new SubstitutionReader(ctx));

        var missing = await adapter.GetMissingIngredientsAsync(recipeId.Value, servings: 2);

        Assert.Empty(missing);
    }

    [Fact(DisplayName = "GetMissingIngredientsAsync returns only the uncovered remainder against the recipe's own product when a real DB-backed substitution edge partially covers the requirement (plantry-aqpa.4)")]
    public async Task GetMissingIngredientsAsync_Remainder_When_Real_Substitution_Edge_Partially_Covers()
    {
        var unitId = Guid.CreateVersion7();
        var chickpeasCanned = Guid.CreateVersion7();
        var chickpeasDried = Guid.CreateVersion7();

        var recipeId = await SeedFlatRecipeAsync(chickpeasCanned, qty: 100m, unitId);
        await SeedSubstitutionEdgeAsync(chickpeasCanned, unitId, chickpeasDried, unitId);

        await using var ctx = NewContext();
        var adapter = BuildAdapter(ctx,
            catalog: FakeCatalog.WithTrackedLeaf(chickpeasCanned, unitId).AddTrackedLeaf(chickpeasDried, unitId, "Chickpeas (dried)"),
            // Need 100; direct 30 + substitute 40 = 70 combined → still short 30.
            stock: new FakeStock().Add(chickpeasCanned, 30m, unitId).Add(chickpeasDried, 40m, unitId),
            prices: new FakePrices(),
            substitutions: new SubstitutionReader(ctx));

        var missing = await adapter.GetMissingIngredientsAsync(recipeId.Value, servings: 2);

        var line = Assert.Single(missing);
        // The list item is the RECIPE'S product (target) — never the substitute.
        Assert.Equal(chickpeasCanned, line.ProductId);
        Assert.NotEqual(chickpeasDried, line.ProductId);
        Assert.Equal(30m, line.Quantity); // 100 required - (30 direct + 40 substitute) = 30
        Assert.Equal(unitId, line.UnitId);
    }

    // ── Home-produced products excluded from the J6 shortfall (plantry-4osq) ──────

    [Fact(DisplayName = "GetMissingIngredientsAsync excludes a Missing home-produced product, keeping the ordinary Missing product (plantry-4osq)")]
    public async Task GetMissingIngredientsAsync_Excludes_Produced_Product()
    {
        var flourId = Guid.CreateVersion7();
        var gardenTomatoesId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();

        var recipeId = await SeedFlatRecipeWithTwoIngredientsAsync(
            flourId, qty1: 200m, gardenTomatoesId, qty2: 3m, unitId);

        await using var ctx = NewContext();
        var adapter = BuildAdapter(ctx,
            // Both tracked, both zero stock → both would read Missing before the produced exclusion.
            catalog: FakeCatalog.WithTrackedLeaf(flourId, unitId)
                .AddTrackedLeaf(gardenTomatoesId, unitId, "Garden Tomatoes")
                .MarkProduced(gardenTomatoesId),
            stock: new FakeStock(),
            prices: new FakePrices());

        var missing = await adapter.GetMissingIngredientsAsync(recipeId.Value, servings: 2);

        var line = Assert.Single(missing);
        Assert.Equal(flourId, line.ProductId);
        Assert.DoesNotContain(missing, m => m.ProductId == gardenTomatoesId);
    }

    [Fact(DisplayName = "GetCandidateEvidenceAsync carries complete cost, fulfillment, and FEFO waste evidence")]
    public async Task GetCandidateEvidenceAsync_Projects_Complete_Cost_And_Fefo_Evidence()
    {
        var recipeId = await SeedFlatRecipeAsync(_cheeseProductId, qty: 2m, _unitId);

        await using var ctx = NewContext();
        var adapter = BuildAdapter(ctx,
            catalog: FakeCatalog.WithTrackedLeaf(_cheeseProductId, _unitId),
            stock: new FakeStock().AddLots(
                _cheeseProductId,
                _unitId,
                new ActiveStockLot(1m, _unitId, Today.AddDays(-1)),
                new ActiveStockLot(10m, _unitId, Today.AddDays(1))),
            prices: FakePrices.With(_cheeseProductId, 0.01m, _unitId));

        var evidence = await adapter.GetCandidateEvidenceAsync(
            [new CandidateRecipeEvidenceRequest(recipeId.Value, Servings: 2)], Today);

        var candidate = Assert.Single(evidence).Value;
        Assert.Equal(0.01m, candidate.CostPerServing);
        Assert.Equal(CandidateCostCompleteness.Complete, candidate.CostCompleteness);
        Assert.Equal(100, candidate.FulfillmentPercent);
        Assert.True(candidate.HasContributingExpiringStock);
    }

    [Fact(DisplayName = "GetCandidateEvidenceAsync marks an under-estimated partial cost without treating it as zero")]
    public async Task GetCandidateEvidenceAsync_Projects_Partial_Cost()
    {
        var pricedProductId = Guid.CreateVersion7();
        var unpricedProductId = Guid.CreateVersion7();
        var recipeId = await SeedFlatRecipeWithTwoIngredientsAsync(
            pricedProductId, qty1: 2m, unpricedProductId, qty2: 2m, _unitId);

        await using var ctx = NewContext();
        var adapter = BuildAdapter(ctx,
            catalog: FakeCatalog.WithTrackedLeaf(pricedProductId, _unitId)
                .AddTrackedLeaf(unpricedProductId, _unitId, "Unpriced Product"),
            stock: new FakeStock()
                .Add(pricedProductId, 2m, _unitId)
                .Add(unpricedProductId, 2m, _unitId),
            prices: FakePrices.With(pricedProductId, 1m, _unitId));

        var evidence = await adapter.GetCandidateEvidenceAsync(
            [new CandidateRecipeEvidenceRequest(recipeId.Value, Servings: 2)], Today);

        var candidate = Assert.Single(evidence).Value;
        Assert.Equal(CandidateCostCompleteness.Partial, candidate.CostCompleteness);
        Assert.Equal(1m, candidate.CostPerServing);
        Assert.NotEqual(0m, candidate.CostPerServing);
    }

    [Fact(DisplayName = "GetCandidateEvidenceAsync leaves an unpriced cost unknown rather than zero")]
    public async Task GetCandidateEvidenceAsync_Projects_Unknown_Cost()
    {
        var productId = Guid.CreateVersion7();
        var recipeId = await SeedFlatRecipeAsync(productId, qty: 2m, _unitId);

        await using var ctx = NewContext();
        var adapter = BuildAdapter(ctx,
            catalog: FakeCatalog.WithTrackedLeaf(productId, _unitId),
            stock: new FakeStock().Add(productId, 2m, _unitId),
            prices: new FakePrices());

        var evidence = await adapter.GetCandidateEvidenceAsync(
            [new CandidateRecipeEvidenceRequest(recipeId.Value, Servings: 2)], Today);

        var candidate = Assert.Single(evidence).Value;
        Assert.Equal(CandidateCostCompleteness.Unknown, candidate.CostCompleteness);
        Assert.Null(candidate.CostPerServing);
    }

    // ── Seeding ──────────────────────────────────────────────────────────────────

    /// <summary>Seeds a flat (no inclusions) recipe with TWO ingredients — for the produced-exclusion
    /// test above, which needs one ordinary and one home-produced product on the same recipe.</summary>
    private async Task<RecipeId> SeedFlatRecipeWithTwoIngredientsAsync(
        Guid productId1, decimal qty1, Guid productId2, decimal qty2, Guid unitId)
    {
        await using var ctx = NewContext();
        var repo = new RecipeRepository(ctx);
        var recipe = Recipe.Create(_household, "Garden Bread", 2, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(productId1, qty1, unitId, null, 0),
            new IngredientLine(productId2, qty2, unitId, null, 1),
        ], Clock);
        await repo.AddAsync(recipe);
        await repo.SaveChangesAsync();
        return recipe.Id;
    }

    /// <summary>Seeds a flat (no inclusions) recipe with a single ingredient — GetMissingIngredientsAsync
    /// still routes through the expanded path (ExpandAsync is a no-op for a flat recipe), matching
    /// production behaviour.</summary>
    private async Task<RecipeId> SeedFlatRecipeAsync(Guid productId, decimal qty, Guid unitId)
    {
        await using var ctx = NewContext();
        var repo = new RecipeRepository(ctx);
        var recipe = Recipe.Create(_household, "Hummus", 2, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, qty, unitId, null, 0)], Clock);
        await repo.AddAsync(recipe);
        await repo.SaveChangesAsync();
        return recipe.Id;
    }

    /// <summary>Seeds a real Substitution edge via the repository (mirrors SubstitutionTests.cs's
    /// seeding pattern) so the adapter's real <see cref="SubstitutionReader"/> resolves it.</summary>
    private async Task SeedSubstitutionEdgeAsync(
        Guid targetProductId, Guid targetUnitId, Guid substituteProductId, Guid substituteUnitId)
    {
        await using var ctx = NewContext();
        var repo = new SubstitutionRepository(ctx);
        var edge = Substitution.Create(
            _household, targetProductId, targetQuantity: 1m, targetUnitId,
            substituteProductId, substituteQuantity: 1m, substituteUnitId, Clock); // 1:1 ratio, same unit
        await repo.AddAsync(edge);
        await repo.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a sub-recipe (DefaultServings 2, one tracked cheese ingredient of 100) and a parent
    /// (DefaultServings 2, NO direct ingredients, includes 2 servings of the sub → factor 1). Returns the
    /// parent id.
    /// </summary>
    private async Task<RecipeId> SeedParentIncludingSubAsync()
    {
        await using var ctx = NewContext();
        var repo = new RecipeRepository(ctx);

        var sub = Recipe.Create(_household, "Cheese Sauce", 2, Clock).Value;
        sub.ReplaceIngredients([new IngredientLine(_cheeseProductId, 100m, _unitId, null, 0)], Clock);
        await repo.AddAsync(sub);

        var parent = Recipe.Create(_household, "Nachos", 2, Clock).Value;
        parent.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(sub.Id, 2m, null, 0)], parent.Id).Value, Clock);
        await repo.AddAsync(parent);

        await repo.SaveChangesAsync();
        return parent.Id;
    }

    private RecipeReadModelAdapter BuildAdapter(
        RecipesDbContext ctx, FakeCatalog catalog, FakeStock stock, FakePrices prices,
        ISubstitutionReader? substitutions = null)
    {
        // The expansion service reads through a repository over the SAME context the adapter queries,
        // mirroring the single scoped RecipesDbContext of a real request.
        var expansion = new RecipeExpansionService(new RecipeRepository(ctx));
        var fulfillment = new FulfillmentService(
            stock, catalog, new IdentityConverter(), new FixedHorizon(7), substitutions ?? new FakeSubstitutions());
        var costing = new CostingService(prices, new IdentityConverter(), catalog);
        return new RecipeReadModelAdapter(ctx, expansion, fulfillment, costing, Clock, new RecipeRatingRepository(ctx), catalog);
    }

    private RecipesDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var ctx = new RecipesDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    // Port fakes (FakeStock, FakeCatalog, FakePrices, IdentityConverter, FixedHorizon, FixedClock) live in
    // RecipeAdapterPortFakes.cs — shared with RecipeReadModelAdapterYieldPhotoTests.cs (plantry-f4dt
    // critic pass 1: this was the 4th private copy of this stub family in this folder).
}
