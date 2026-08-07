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
/// L3 integration tests for <see cref="RecipeReadModelAdapter.FindSoleYieldPhotoRecipeIdsAsync"/>
/// (plantry-f4dt) — the meal-plan card's product-dish photo-inheritance lookup. Exercises the real
/// grouping/HasPhoto query over a Postgres-backed <see cref="RecipesDbContext"/>: exactly one
/// producer-recipe with a photo → included; zero, many, or a photo-less sole producer → omitted.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeReadModelAdapterYieldPhotoTests(PostgresFixture db) : IAsyncLifetime
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

    [Fact(DisplayName = "FindSoleYieldPhotoRecipeIdsAsync includes a product with exactly one photo-bearing producer-recipe")]
    public async Task Includes_Sole_Producer_With_Photo()
    {
        var productId = Guid.CreateVersion7();
        RecipeId recipeId;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            recipe.SetYield(productId, 1m, Guid.CreateVersion7(), Clock);
            recipe.SetPhoto([1, 2, 3], "image/png", null, Clock);
            await repo.AddAsync(recipe);
            await repo.SaveChangesAsync();
            recipeId = recipe.Id;
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.FindSoleYieldPhotoRecipeIdsAsync([productId]);

        var pair = Assert.Single(result);
        Assert.Equal(productId, pair.Key);
        Assert.Equal(recipeId.Value, pair.Value);
    }

    [Fact(DisplayName = "FindSoleYieldPhotoRecipeIdsAsync omits a product whose sole producer-recipe has no photo")]
    public async Task Omits_Sole_Producer_Without_Photo()
    {
        var productId = Guid.CreateVersion7();
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            recipe.SetYield(productId, 1m, Guid.CreateVersion7(), Clock);
            // No SetPhoto call.
            await repo.AddAsync(recipe);
            await repo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.FindSoleYieldPhotoRecipeIdsAsync([productId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "FindSoleYieldPhotoRecipeIdsAsync omits a product with multiple producer-recipes, even if all have photos")]
    public async Task Omits_Product_With_Multiple_Producers()
    {
        var productId = Guid.CreateVersion7();
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);

            var recipeA = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            recipeA.SetYield(productId, 1m, Guid.CreateVersion7(), Clock);
            recipeA.SetPhoto([1, 2, 3], "image/png", null, Clock);
            await repo.AddAsync(recipeA);

            var recipeB = Recipe.Create(_household, "Baked Ziti", 4, Clock).Value;
            recipeB.SetYield(productId, 1m, Guid.CreateVersion7(), Clock);
            recipeB.SetPhoto([4, 5, 6], "image/png", null, Clock);
            await repo.AddAsync(recipeB);

            await repo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.FindSoleYieldPhotoRecipeIdsAsync([productId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "FindSoleYieldPhotoRecipeIdsAsync omits a product with zero producer-recipes")]
    public async Task Omits_Product_With_No_Producers()
    {
        var productId = Guid.CreateVersion7();
        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.FindSoleYieldPhotoRecipeIdsAsync([productId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "FindSoleYieldPhotoRecipeIdsAsync resolves a batch of multiple product ids in one call")]
    public async Task Resolves_Batch_Of_Multiple_Products()
    {
        var soleWithPhoto = Guid.CreateVersion7();
        var soleWithoutPhoto = Guid.CreateVersion7();
        var multiProducer = Guid.CreateVersion7();
        RecipeId soleWithPhotoRecipeId;

        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);

            var r1 = Recipe.Create(_household, "R1", 4, Clock).Value;
            r1.SetYield(soleWithPhoto, 1m, Guid.CreateVersion7(), Clock);
            r1.SetPhoto([1], "image/png", null, Clock);
            await repo.AddAsync(r1);
            soleWithPhotoRecipeId = r1.Id;

            var r2 = Recipe.Create(_household, "R2", 4, Clock).Value;
            r2.SetYield(soleWithoutPhoto, 1m, Guid.CreateVersion7(), Clock);
            await repo.AddAsync(r2);

            var r3a = Recipe.Create(_household, "R3a", 4, Clock).Value;
            r3a.SetYield(multiProducer, 1m, Guid.CreateVersion7(), Clock);
            r3a.SetPhoto([2], "image/png", null, Clock);
            await repo.AddAsync(r3a);
            var r3b = Recipe.Create(_household, "R3b", 4, Clock).Value;
            r3b.SetYield(multiProducer, 1m, Guid.CreateVersion7(), Clock);
            r3b.SetPhoto([3], "image/png", null, Clock);
            await repo.AddAsync(r3b);

            await repo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.FindSoleYieldPhotoRecipeIdsAsync(
            [soleWithPhoto, soleWithoutPhoto, multiProducer]);

        var pair = Assert.Single(result);
        Assert.Equal(soleWithPhoto, pair.Key);
        Assert.Equal(soleWithPhotoRecipeId.Value, pair.Value);
    }

    [Fact(DisplayName = "FindSoleYieldPhotoRecipeIdsAsync omits a product whose only photo-bearing producer-recipe is archived")]
    public async Task Omits_Product_Whose_Sole_Producer_Is_Archived()
    {
        var productId = Guid.CreateVersion7();
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            recipe.SetYield(productId, 1m, Guid.CreateVersion7(), Clock);
            recipe.SetPhoto([1, 2, 3], "image/png", null, Clock);
            recipe.Archive(Clock);
            await repo.AddAsync(recipe);
            await repo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.FindSoleYieldPhotoRecipeIdsAsync([productId]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "FindSoleYieldPhotoRecipeIdsAsync resolves the sole NON-archived producer when another producer-recipe is archived")]
    public async Task Resolves_NonArchived_Producer_When_Another_Producer_Is_Archived()
    {
        var productId = Guid.CreateVersion7();
        RecipeId nonArchivedRecipeId;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);

            var archived = Recipe.Create(_household, "Lasagna (old)", 4, Clock).Value;
            archived.SetYield(productId, 1m, Guid.CreateVersion7(), Clock);
            archived.SetPhoto([1, 2, 3], "image/png", null, Clock);
            archived.Archive(Clock);
            await repo.AddAsync(archived);

            var active = Recipe.Create(_household, "Lasagna", 4, Clock).Value;
            active.SetYield(productId, 1m, Guid.CreateVersion7(), Clock);
            active.SetPhoto([4, 5, 6], "image/png", null, Clock);
            await repo.AddAsync(active);
            nonArchivedRecipeId = active.Id;

            await repo.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var adapter = BuildAdapter(readCtx);

        var result = await adapter.FindSoleYieldPhotoRecipeIdsAsync([productId]);

        var pair = Assert.Single(result);
        Assert.Equal(productId, pair.Key);
        Assert.Equal(nonArchivedRecipeId.Value, pair.Value);
    }

    private RecipeReadModelAdapter BuildAdapter(RecipesDbContext ctx)
    {
        // The photo lookup path never touches expansion/fulfillment/costing, but the adapter's
        // constructor requires them — construct with the shared (unused) port fakes over the same
        // context (RecipeAdapterPortFakes.cs), mirroring RecipeReadModelAdapterExpandedTests's
        // fixture-sharing pattern.
        var catalog = new FakeCatalog();
        var expansion = new RecipeExpansionService(new RecipeRepository(ctx));
        var fulfillment = new FulfillmentService(new FakeStock(), catalog, new IdentityConverter(), new FixedHorizon(7), new FakeSubstitutions());
        var costing = new CostingService(new FakePrices(), new IdentityConverter(), catalog);
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
}
