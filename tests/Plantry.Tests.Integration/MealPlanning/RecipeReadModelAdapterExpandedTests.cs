using Microsoft.EntityFrameworkCore;
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

    // ── Seeding ──────────────────────────────────────────────────────────────────

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
        RecipesDbContext ctx, FakeCatalog catalog, FakeStock stock, FakePrices prices)
    {
        // The expansion service reads through a repository over the SAME context the adapter queries,
        // mirroring the single scoped RecipesDbContext of a real request.
        var expansion = new RecipeExpansionService(new RecipeRepository(ctx));
        var fulfillment = new FulfillmentService(stock, catalog, new IdentityConverter(), new FixedHorizon(7), new FakeSubstitutions());
        var costing = new CostingService(prices, new IdentityConverter(), catalog);
        return new RecipeReadModelAdapter(ctx, expansion, fulfillment, costing, Clock, new RecipeRatingRepository(ctx));
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
