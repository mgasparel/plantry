using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Application;
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
/// L3 integration tests for <see cref="RecipeReadModelAdapter.GetRatingSummariesAsync"/> (plantry-zlwp.5) —
/// the batched rating lookup <see cref="Plantry.MealPlanning.Application.GeneratePlanService"/> uses to
/// enrich <see cref="Plantry.MealPlanning.Domain.CandidateRecipe"/> with household/attendee rating signal.
/// Exercises the real EF query + <see cref="RecipeRatingRepository"/> against a Postgres-backed
/// <see cref="RecipesDbContext"/> (mirrors <see cref="RecipeReadModelAdapterBatchTests"/>'s rationale for
/// this same adapter's other batched member — a pure-fake-backed unit test proves nothing about the
/// GroupBy/HasQueryFilter interaction).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeReadModelAdapterRatingSummaryTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "GetRatingSummariesAsync computes StarsByUserId/HouseholdAvg(1dp)/RatedCount for a rated recipe")]
    public async Task Computes_Summary_For_Rated_Recipe()
    {
        RecipeId recipeId;
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await using (var ctx = NewContext())
        {
            var recipeRepo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            await recipeRepo.AddAsync(recipe);
            recipeId = recipe.Id;
            await recipeRepo.SaveChangesAsync();

            var ratingRepo = new RecipeRatingRepository(ctx);
            await ratingRepo.AddAsync(RecipeRating.Create(_household, recipeId, alice, 5, Clock));
            await ratingRepo.AddAsync(RecipeRating.Create(_household, recipeId, bob, 2, Clock));
            await ratingRepo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.GetRatingSummariesAsync([recipeId.Value]);

        var summary = Assert.Single(result).Value;
        Assert.Equal(2, summary.RatedCount);
        Assert.Equal(3.5m, summary.HouseholdAvg); // (5+2)/2 = 3.5, already 1dp
        Assert.Equal(5, summary.StarsByUserId[alice]);
        Assert.Equal(2, summary.StarsByUserId[bob]);
    }

    [Fact(DisplayName = "GetRatingSummariesAsync omits an unrated recipe id — absent, not a default/sentinel entry")]
    public async Task Omits_Unrated_Recipe()
    {
        RecipeId recipeId;
        await using (var ctx = NewContext())
        {
            var recipeRepo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Pancakes", 2, Clock).Value;
            await recipeRepo.AddAsync(recipe);
            recipeId = recipe.Id;
            await recipeRepo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.GetRatingSummariesAsync([recipeId.Value]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetRatingSummariesAsync batches multiple recipe ids in one round-trip, each summary independent")]
    public async Task Batches_Multiple_Recipes()
    {
        RecipeId recipe1, recipe2;
        var user = Guid.NewGuid();

        await using (var ctx = NewContext())
        {
            var recipeRepo = new RecipeRepository(ctx);
            var r1 = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            var r2 = Recipe.Create(_household, "Pancakes", 2, Clock).Value;
            await recipeRepo.AddAsync(r1);
            await recipeRepo.AddAsync(r2);
            recipe1 = r1.Id;
            recipe2 = r2.Id;
            await recipeRepo.SaveChangesAsync();

            var ratingRepo = new RecipeRatingRepository(ctx);
            await ratingRepo.AddAsync(RecipeRating.Create(_household, recipe1, user, 4, Clock));
            await ratingRepo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.GetRatingSummariesAsync([recipe1.Value, recipe2.Value]);

        var only = Assert.Single(result);
        Assert.Equal(recipe1.Value, only.Key);
        Assert.Equal(4m, only.Value.HouseholdAvg);
        Assert.False(result.ContainsKey(recipe2.Value));
    }

    [Fact(DisplayName = "GetRatingSummariesAsync returns an empty dictionary for an empty id collection, without throwing")]
    public async Task Empty_Collection_Returns_Empty_Dictionary()
    {
        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.GetRatingSummariesAsync([]);

        Assert.Empty(result);
    }

    private RecipeReadModelAdapter BuildAdapter(RecipesDbContext ctx)
    {
        // GetRatingSummariesAsync never touches expansion/fulfillment/costing, but the adapter's
        // constructor requires them — construct with the shared (unused) port fakes over the same
        // context (RecipeAdapterPortFakes.cs), mirroring RecipeReadModelAdapterBatchTests's fixture-sharing.
        var expansion = new RecipeExpansionService(new RecipeRepository(ctx));
        var fulfillment = new FulfillmentService(new FakeStock(), new FakeCatalog(), new IdentityConverter(), new FixedHorizon(7));
        var costing = new CostingService(new FakePrices(), new IdentityConverter(), new FakeCatalog());
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
}
