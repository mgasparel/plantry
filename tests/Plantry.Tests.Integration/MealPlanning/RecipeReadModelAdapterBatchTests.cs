using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Application;
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
/// L3 integration tests for <see cref="RecipeReadModelAdapter.GetByIdsAsync"/> and the
/// <see cref="RecipeReadModel.CookTimeMinutes"/> projection on <see cref="RecipeReadModelAdapter.GetByIdAsync"/>
/// (plantry-r2yf) — the batched recipe-dish resolution Today's planned-meals band uses to eliminate
/// its per-dish N+1. Exercises the real EF query against a Postgres-backed <see cref="RecipesDbContext"/>:
/// the <c>HashSet&lt;RecipeId&gt;.Contains(r.Id)</c> predicate shape must actually translate against
/// this context's <c>HouseholdId</c>-based <c>HasQueryFilter</c> — a pure-fake-backed unit test proves
/// nothing about that translation (mirrors the house pattern established for this same adapter's
/// other batched member, <see cref="RecipeReadModelAdapterYieldPhotoTests"/>).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeReadModelAdapterBatchTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    /// <summary>Fixed, not <c>SystemClock.Instance</c> (missing-seam:iclock-web, plantry-4tb4) — this file's
    /// adapter calls are clock-inert (no path here reads "today"), but the constructor is now clock-bearing,
    /// so a fixed clock keeps every test double in this folder off the real wall clock regardless (gate 10).</summary>
    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "GetByIdsAsync resolves multiple non-archived recipes in one call, keyed by id, with Name/HasPhoto/CookTimeMinutes")]
    public async Task Resolves_Multiple_NonArchived_Recipes_With_CookTimeMinutes()
    {
        RecipeId id1, id2, archivedId;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);

            var r1 = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            r1.SetCookTime(45, Clock);
            r1.SetPhoto([1, 2, 3], "image/png", null, Clock);
            await repo.AddAsync(r1);
            id1 = r1.Id;

            var r2 = Recipe.Create(_household, "Pancakes", 2, Clock).Value;
            r2.SetCookTime(15, Clock);
            await repo.AddAsync(r2);
            id2 = r2.Id;

            var archived = Recipe.Create(_household, "Retired Stew", 4, Clock).Value;
            archived.SetCookTime(90, Clock);
            archived.Archive(Clock);
            await repo.AddAsync(archived);
            archivedId = archived.Id;

            await repo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.GetByIdsAsync([id1.Value, id2.Value, archivedId.Value]);

        Assert.Equal(2, result.Count);

        var lasagna = result[id1.Value];
        Assert.Equal("Lasagna", lasagna.Name);
        Assert.True(lasagna.HasPhoto);
        Assert.Equal(45, lasagna.CookTimeMinutes);

        var pancakes = result[id2.Value];
        Assert.Equal("Pancakes", pancakes.Name);
        Assert.False(pancakes.HasPhoto);
        Assert.Equal(15, pancakes.CookTimeMinutes);

        // Archived recipes are omitted, same as GetByIdAsync's ArchivedAt == null filter.
        Assert.False(result.ContainsKey(archivedId.Value));
    }

    [Fact(DisplayName = "GetByIdsAsync omits an id that does not exist — absent, not a default/sentinel entry")]
    public async Task Omits_NonExistent_Id()
    {
        RecipeId existingId;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            await repo.AddAsync(recipe);
            existingId = recipe.Id;
            await repo.SaveChangesAsync();
        }

        var missingId = Guid.CreateVersion7();

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.GetByIdsAsync([existingId.Value, missingId]);

        var pair = Assert.Single(result);
        Assert.Equal(existingId.Value, pair.Key);
        Assert.False(result.ContainsKey(missingId));
    }

    [Fact(DisplayName = "GetByIdsAsync returns an empty dictionary for an empty id collection, without throwing")]
    public async Task Empty_Collection_Returns_Empty_Dictionary()
    {
        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.GetByIdsAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "GetByIdAsync projects CookTimeMinutes from the seeded recipe")]
    public async Task GetByIdAsync_Projects_CookTimeMinutes()
    {
        RecipeId recipeId;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            recipe.SetCookTime(45, Clock);
            await repo.AddAsync(recipe);
            recipeId = recipe.Id;
            await repo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.GetByIdAsync(recipeId.Value);

        Assert.NotNull(result);
        Assert.Equal(45, result!.CookTimeMinutes);
    }

    private RecipeReadModelAdapter BuildAdapter(RecipesDbContext ctx)
    {
        // GetByIdsAsync/GetByIdAsync never touch expansion/fulfillment/costing, but the adapter's
        // constructor requires them — construct with the shared (unused) port fakes over the same
        // context (RecipeAdapterPortFakes.cs), mirroring RecipeReadModelAdapterYieldPhotoTests's
        // fixture-sharing pattern.
        var expansion = new RecipeExpansionService(new RecipeRepository(ctx));
        var fulfillment = new FulfillmentService(new FakeStock(), new FakeCatalog(), new IdentityConverter(), new FixedHorizon(7), new FakeSubstitutions());
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
