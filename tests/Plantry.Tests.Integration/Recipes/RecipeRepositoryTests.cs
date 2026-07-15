using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration tests proving the <see cref="Recipe"/> aggregate — its ingredient collection,
/// tag membership, and 1:1 photo — round-trips through EF against a real Postgres schema
/// (recipes-domain-model.md §3; P2-1 acceptance criteria).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeRepositoryTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly IClock _clock = SystemClock.Instance;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Full round-trip (ingredients + tags + photo) ──────────────────────────

    [Fact(DisplayName = "Recipe round-trips with ingredients, tag memberships, and photo through EF")]
    public async Task Recipe_RoundTrips_With_Ingredients_Tags_And_Photo()
    {
        // Seed tag rows the membership FKs will reference
        var tagId1 = TagId.New();
        var tagId2 = TagId.New();
        await SeedTagAsync(tagId1, "Italian");
        await SeedTagAsync(tagId2, "Quick");

        var productId1 = Guid.CreateVersion7();
        var productId2 = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();

        RecipeId recipeId;

        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);

            var recipe = Recipe.Create(_household, "Pasta Bolognese", 4, _clock).Value;
            recipe.SetSource("https://example.com/pasta", _clock);
            recipe.SetCookTime(45, _clock);
            recipe.SetDirections("Cook pasta. Add sauce.", _clock);
            recipe.SetTags([tagId1, tagId2], _clock);
            recipe.ReplaceIngredients(
            [
                new IngredientLine(productId1, 500m, unitId, null, 0),
                new IngredientLine(productId2, null, null, "To taste", 1),
            ], _clock);
            recipe.SetPhoto([1, 2, 3, 4], "image/jpeg", [0xFF, 0xFE], _clock);

            recipeId = recipe.Id;
            await repo.AddAsync(recipe);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var repo2 = new RecipeRepository(ctx2);
        var loaded = await repo2.GetByIdAsync(recipeId);

        Assert.NotNull(loaded);
        Assert.Equal(_household, loaded!.HouseholdId);
        Assert.Equal("Pasta Bolognese", loaded.Name);
        Assert.Equal("https://example.com/pasta", loaded.Source);
        Assert.Equal(45, loaded.CookTimeMinutes);
        Assert.Equal("Cook pasta. Add sauce.", loaded.Directions);
        Assert.Equal(4, loaded.DefaultServings);

        // Ingredients
        Assert.Equal(2, loaded.Ingredients.Count);
        Assert.All(loaded.Ingredients, i => Assert.Equal(_household, i.HouseholdId));
        Assert.All(loaded.Ingredients, i => Assert.Equal(recipeId, i.RecipeId));
        var firstIng = loaded.Ingredients.Single(i => i.Ordinal == 0);
        Assert.Equal(productId1, firstIng.ProductId);
        Assert.Equal(500m, firstIng.Quantity);
        Assert.Equal(unitId, firstIng.UnitId);
        var secondIng = loaded.Ingredients.Single(i => i.Ordinal == 1);
        Assert.Null(secondIng.Quantity);
        Assert.Null(secondIng.UnitId);
        Assert.Equal("To taste", secondIng.GroupHeading);

        // Tags
        Assert.Equal(2, loaded.Tags.Count);
        Assert.Contains(loaded.Tags, t => t.TagId == tagId1);
        Assert.Contains(loaded.Tags, t => t.TagId == tagId2);
        Assert.All(loaded.Tags, t => Assert.Equal(_household, t.HouseholdId));

        // Photo
        Assert.NotNull(loaded.Photo);
        Assert.Equal([1, 2, 3, 4], loaded.Photo!.Content);
        Assert.Equal("image/jpeg", loaded.Photo.ContentType);
        Assert.Equal([0xFF, 0xFE], loaded.Photo.Sha256);
        Assert.Equal(recipeId, loaded.Photo.Id);
    }

    [Fact(DisplayName = "Recipe with no photo round-trips correctly")]
    public async Task Recipe_Without_Photo_RoundTrips()
    {
        RecipeId recipeId;

        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Simple Soup", 2, _clock).Value;
            recipe.ReplaceIngredients(
            [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
            recipeId = recipe.Id;
            await repo.AddAsync(recipe);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var loaded = await new RecipeRepository(ctx2).GetByIdAsync(recipeId);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.Photo);
    }

    [Fact(DisplayName = "Recipe with no tags round-trips correctly")]
    public async Task Recipe_Without_Tags_RoundTrips()
    {
        RecipeId recipeId;

        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Egg on Toast", 1, _clock).Value;
            recipe.ReplaceIngredients(
            [new IngredientLine(Guid.CreateVersion7(), 2m, Guid.CreateVersion7(), null, 0)], _clock);
            recipeId = recipe.Id;
            await repo.AddAsync(recipe);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var loaded = await new RecipeRepository(ctx2).GetByIdAsync(recipeId);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Tags);
    }

    [Fact(DisplayName = "GetByIdAsync returns null when recipe does not exist")]
    public async Task GetByIdAsync_Returns_Null_When_Not_Found()
    {
        await using var ctx = NewContext();
        var loaded = await new RecipeRepository(ctx).GetByIdAsync(RecipeId.New());

        Assert.Null(loaded);
    }

    [Fact(DisplayName = "NameExistsAsync returns true for an existing name and false for an absent one")]
    public async Task NameExistsAsync_Detects_Existing_Names()
    {
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var recipe = Recipe.Create(_household, "Lasagne", 6, _clock).Value;
            recipe.ReplaceIngredients(
            [new IngredientLine(Guid.CreateVersion7(), 300m, Guid.CreateVersion7(), null, 0)], _clock);
            await repo.AddAsync(recipe);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var repo2 = new RecipeRepository(ctx2);

        Assert.True(await repo2.NameExistsAsync(_household, "Lasagne"));
        Assert.False(await repo2.NameExistsAsync(_household, "Tiramisu"));
    }

    [Fact(DisplayName = "RecipeCreated domain event is emitted after Create")]
    public void Recipe_Create_Emits_RecipeCreated_Event()
    {
        var recipe = Recipe.Create(_household, "Tiramisu", 8, _clock).Value;

        var evt = Assert.Single(recipe.DomainEvents);
        var created = Assert.IsType<RecipeCreatedEvent>(evt);
        Assert.Equal(recipe.Id, created.RecipeId);
        Assert.Equal(_household, created.HouseholdId);
    }

    [Fact(DisplayName = "ListRecipeIdsWithPhotoAsync returns only ids of recipes that have a photo")]
    public async Task ListRecipeIdsWithPhotoAsync_Returns_Only_Photo_Recipe_Ids()
    {
        // Seed two recipes: one with a photo, one without.
        RecipeId withPhotoId;
        RecipeId withoutPhotoId;

        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);

            var withPhoto = Recipe.Create(_household, "Photo Recipe", 2, _clock).Value;
            withPhoto.ReplaceIngredients(
                [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
            withPhoto.SetPhoto([0xDE, 0xAD], "image/jpeg", null, _clock);
            withPhotoId = withPhoto.Id;
            await repo.AddAsync(withPhoto);

            var withoutPhoto = Recipe.Create(_household, "No Photo Recipe", 2, _clock).Value;
            withoutPhoto.ReplaceIngredients(
                [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
            withoutPhotoId = withoutPhoto.Id;
            await repo.AddAsync(withoutPhoto);

            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var photoIds = await new RecipeRepository(ctx2).ListRecipeIdsWithPhotoAsync();

        Assert.Contains(withPhotoId, photoIds);
        Assert.DoesNotContain(withoutPhotoId, photoIds);
    }

    // ── Inclusions (recipe-composition.md) ──────────────────────────────────────

    [Fact(DisplayName = "Recipe with an inclusion round-trips through EF (new recipe_inclusion table)")]
    public async Task Recipe_With_Inclusion_RoundTrips()
    {
        // Seed the sub-recipe the inclusion FK will reference.
        RecipeId subId;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var sub = Recipe.Create(_household, "Nacho Cheese", 4, _clock).Value;
            sub.ReplaceIngredients(
                [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
            subId = sub.Id;
            await repo.AddAsync(sub);
            await repo.SaveChangesAsync();
        }

        RecipeId parentId;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var parent = Recipe.Create(_household, "Nachos Deluxe", 2, _clock).Value;
            parent.ReplaceLines(
                RecipeLineSet.Create(
                    [new IngredientLine(Guid.CreateVersion7(), 200m, Guid.CreateVersion7(), null, 0)],
                    [new InclusionLine(subId, 2m, "Toppings", 1)],
                    parent.Id).Value,
                _clock);
            parentId = parent.Id;
            await repo.AddAsync(parent);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var loaded = await new RecipeRepository(ctx2).GetByIdAsync(parentId);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Ingredients);
        var inc = Assert.Single(loaded.Inclusions);
        Assert.Equal(_household, inc.HouseholdId);
        Assert.Equal(parentId, inc.RecipeId);
        Assert.Equal(subId, inc.SubRecipeId);
        Assert.Equal(2m, inc.Servings);
        Assert.Equal("Toppings", inc.GroupHeading);
        Assert.Equal(1, inc.Ordinal);
    }

    [Fact(DisplayName = "Inclusions are wholesale-replaced with re-minted ids on re-save")]
    public async Task Inclusions_Are_Wholesale_Replaced_On_Resave()
    {
        RecipeId subA, subB, parentId;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var a = Recipe.Create(_household, "Base A", 4, _clock).Value;
            a.ReplaceIngredients([new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
            var b = Recipe.Create(_household, "Base B", 4, _clock).Value;
            b.ReplaceIngredients([new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
            subA = a.Id; subB = b.Id;
            await repo.AddAsync(a);
            await repo.AddAsync(b);

            var parent = Recipe.Create(_household, "Assembly", 2, _clock).Value;
            parent.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(subA, 1m, null, 0)], parent.Id).Value, _clock);
            parentId = parent.Id;
            await repo.AddAsync(parent);
            await repo.SaveChangesAsync();
        }

        // Re-save with a different inclusion set.
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var parent = await repo.GetByIdAsync(parentId);
            parent!.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(subB, 3m, null, 0)], parent.Id).Value, _clock);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var loaded = await new RecipeRepository(ctx2).GetByIdAsync(parentId);
        var inc = Assert.Single(loaded!.Inclusions);
        Assert.Equal(subB, inc.SubRecipeId);
        Assert.Equal(3m, inc.Servings);
    }

    [Fact(DisplayName = "GetIncluderIdsAsync returns direct and transitive includers of a sub-recipe")]
    public async Task GetIncluderIdsAsync_Returns_Direct_And_Transitive_Includers()
    {
        // Chain: A → B → C (A includes B, B includes C).
        RecipeId a, b, c;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var rc = Recipe.Create(_household, "C", 4, _clock).Value;
            rc.ReplaceIngredients([new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
            c = rc.Id;
            await repo.AddAsync(rc);

            var rb = Recipe.Create(_household, "B", 4, _clock).Value;
            rb.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(c, 1m, null, 0)], rb.Id).Value, _clock);
            b = rb.Id;
            await repo.AddAsync(rb);

            var ra = Recipe.Create(_household, "A", 4, _clock).Value;
            ra.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(b, 1m, null, 0)], ra.Id).Value, _clock);
            a = ra.Id;
            await repo.AddAsync(ra);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var repo2 = new RecipeRepository(ctx2);

        var directIncludersOfC = await repo2.GetIncluderIdsAsync(c);
        Assert.Equal([b], directIncludersOfC);

        var transitiveIncludersOfC = await repo2.GetIncluderIdsAsync(c, transitive: true);
        Assert.Equal(new HashSet<RecipeId> { a, b }, transitiveIncludersOfC);

        // A sub nobody includes returns an empty set.
        Assert.Empty(await repo2.GetIncluderIdsAsync(a));
    }

    [Fact(DisplayName = "ListInclusionEdgesAsync returns every parent→sub edge for the household")]
    public async Task ListInclusionEdgesAsync_Returns_All_Edges()
    {
        RecipeId a, b, sub;
        await using (var ctx = NewContext())
        {
            var repo = new RecipeRepository(ctx);
            var rSub = Recipe.Create(_household, "Shared Base", 4, _clock).Value;
            rSub.ReplaceIngredients([new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], _clock);
            sub = rSub.Id;
            await repo.AddAsync(rSub);

            var ra = Recipe.Create(_household, "A", 4, _clock).Value;
            ra.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(sub, 1m, null, 0)], ra.Id).Value, _clock);
            a = ra.Id;
            await repo.AddAsync(ra);

            var rb = Recipe.Create(_household, "B", 4, _clock).Value;
            rb.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(sub, 2m, null, 0)], rb.Id).Value, _clock);
            b = rb.Id;
            await repo.AddAsync(rb);
            await repo.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        var edges = await new RecipeRepository(ctx2).ListInclusionEdgesAsync();

        Assert.Equal(2, edges.Count);
        Assert.Contains(new RecipeInclusionEdge(a, sub), edges);
        Assert.Contains(new RecipeInclusionEdge(b, sub), edges);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private RecipesDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var ctx = new RecipesDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private async Task SeedTagAsync(TagId tagId, string name)
    {
        // Use Npgsql directly — test seeding seam, no domain behaviour needed.
        await using var conn = new Npgsql.NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recipes.tag (tag_id, household_id, name, created_at, updated_at)
            VALUES (@tag_id, @household_id, @name, now(), now())
            ON CONFLICT DO NOTHING
            """;
        cmd.Parameters.AddWithValue("tag_id", tagId.Value);
        cmd.Parameters.AddWithValue("household_id", _household.Value);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
    }
}
