using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Migration.Grocy;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Unit.Recipes.Application;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="RecipeCommitService"/> covering:
/// - Commit logic: staged recipes with resolved crosswalk entries are committed via AuthorRecipe.
/// - Photo attachment: photo bytes are written to the recipe aggregate after commit.
/// - Idempotency: a second run with the same crosswalk data upserts rather than duplicates.
/// - Crosswalk-missing skip: ingredients with null PlantryProductId/PlantryUnitId are omitted.
/// - Entire-recipe skip: recipes where ALL ingredients are crosswalk-missing are skipped (R3).
/// - DuplicateName idempotency: a recipe that reports DuplicateName is resolved via name lookup.
/// </summary>
public sealed class RecipeCommitServiceTests
{
    // ──────────── Common test values ────────────────────────────────────────

    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly IClock Clock = SystemClock.Instance;

    private static readonly Guid ProductAId = Guid.CreateVersion7();
    private static readonly Guid ProductBId = Guid.CreateVersion7();
    private static readonly Guid UnitId     = Guid.CreateVersion7();

    // ──────────── Test harness ───────────────────────────────────────────────

    /// <summary>
    /// Wires up a complete <see cref="RecipeCommitService"/> using the same fake
    /// collaborators as <see cref="AuthorRecipeTests"/>. Exposes the inner repositories
    /// so tests can inspect state.
    /// </summary>
    private sealed class Harness
    {
        public FakeRecipeRepository Recipes    { get; }
        public FakeCatalogProductReader Products { get; }
        public RecipeCommitService Service     { get; }

        public Harness(bool authenticated = true)
        {
            Recipes = new FakeRecipeRepository();
            var tags      = new FakeTagRepository();
            Products = new FakeCatalogProductReader();
            var converter = new FakeUnitConverter();
            var writer    = new FakeCatalogWriter(Products, converter);
            var tenant    = new FakeTenantContext(authenticated ? HouseholdGuid : (Guid?)null);
            var lineResolver = new IngredientLineResolver(Products, writer);
            var conversionPlanner = new ConversionGapPlanner(converter, writer);
            var author    = new AuthorRecipe(Recipes, tags, Products, writer, lineResolver, conversionPlanner, Clock, tenant, NullLogger<AuthorRecipe>.Instance);

            Service = new RecipeCommitService(author, Recipes, Clock, tenant);
        }
    }

    // Pre-register the two test products with same default unit so no conversion check is needed.
    private static Harness BuildHarness()
    {
        var h = new Harness();
        // Both products use the same UnitId as their default unit — so the committed
        // ingredient unit == product default unit; no cross-dimension conversion needed.
        h.Products.Register(new CatalogProduct(ProductAId, "Flour",  TrackStock: true,  UnitId, null, false, []));
        h.Products.Register(new CatalogProduct(ProductBId, "Butter", TrackStock: true,  UnitId, null, false, []));
        return h;
    }

    // ──────────── Happy-path: single recipe committed ────────────────────────

    [Fact]
    public async Task CommitAsync_Single_Recipe_Creates_Recipe_With_Ingredients()
    {
        var h = BuildHarness();

        var row = MakeRow(1, "Pancakes", ingredients:
        [
            MakeIngredient(1, ProductAId, UnitId, 200m),
            MakeIngredient(2, ProductBId, UnitId, 50m),
        ]);

        var (results, _) = await h.Service.CommitAsync([row], ManifestPath(), default);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.False(result.Skipped);
        Assert.Equal(1, result.GrocyId);
        Assert.Equal("Pancakes", result.GrocyName);
        Assert.NotNull(result.PlantryRecipeId);

        var recipe = Assert.Single(h.Recipes.Items);
        Assert.Equal("Pancakes", recipe.Name);
        Assert.Equal(2, recipe.Ingredients.Count);
        Assert.Equal(RecipeCommitService.PhotoCommitDisposition.NoneStaged, result.PhotoDisposition);
    }

    // ──────────── Ingredient ordinal reindexing ───────────────────────────────

    [Fact]
    public async Task CommitAsync_Reindexes_Ordinals_After_Crosswalk_Skip()
    {
        var h = BuildHarness();

        // Ingredient at ordinal 0 has no crosswalk (both IDs null); ordinals 1 and 2 commit.
        var row = MakeRow(10, "Soup", ingredients:
        [
            MakeIngredient(1, null, null, 1m),           // crosswalk-missing
            MakeIngredient(2, ProductAId, UnitId, 200m, ordinal: 1),
            MakeIngredient(3, ProductBId, UnitId, 50m,  ordinal: 2),
        ]);

        var (results, _) = await h.Service.CommitAsync([row], ManifestPath(), default);

        var result = Assert.Single(results);
        Assert.True(result.Success);

        var ingResults = result.Ingredients;
        Assert.Equal(3, ingResults.Count);
        Assert.Equal(RecipeCommitService.IngredientCommitDisposition.SkippedCrosswalkMissing, ingResults[0].Disposition);
        Assert.Equal(RecipeCommitService.IngredientCommitDisposition.Committed,               ingResults[1].Disposition);
        Assert.Equal(RecipeCommitService.IngredientCommitDisposition.Committed,               ingResults[2].Disposition);

        var recipe = Assert.Single(h.Recipes.Items);
        // After skip, ordinals must be contiguous from 0 (R6)
        Assert.Equal(2, recipe.Ingredients.Count);
        Assert.Equal([0, 1], recipe.Ingredients.OrderBy(i => i.Ordinal).Select(i => i.Ordinal));
    }

    // ──────────── Skip when all ingredients are crosswalk-missing (R3) ────────

    [Fact]
    public async Task CommitAsync_Skips_Recipe_When_All_Ingredients_Missing_Crosswalk()
    {
        var h = BuildHarness();

        var row = MakeRow(2, "Mystery Dish", ingredients:
        [
            MakeIngredient(1, null, null, 1m),
            MakeIngredient(2, ProductAId, null, 2m), // unit null → crosswalk-missing
        ]);

        var (results, _) = await h.Service.CommitAsync([row], ManifestPath(), default);

        var result = Assert.Single(results);
        Assert.True(result.Skipped);
        Assert.True(result.Success); // skipped is not a failure
        Assert.Null(result.PlantryRecipeId);
        Assert.Empty(h.Recipes.Items);
    }

    // ──────────── Photo attachment ────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_Attaches_Photo_After_Recipe_Saved()
    {
        var h = BuildHarness();

        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header
        var row = MakeRow(3, "Chocolate Cake",
            ingredients: [MakeIngredient(1, ProductAId, UnitId, 300m)],
            photoBytes: photoBytes,
            photoContentType: "image/jpeg");

        var (results, _) = await h.Service.CommitAsync([row], ManifestPath(), default);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.Equal(RecipeCommitService.PhotoCommitDisposition.Committed, result.PhotoDisposition);

        var recipe = Assert.Single(h.Recipes.Items);
        Assert.NotNull(recipe.Photo);
        Assert.Equal(photoBytes, recipe.Photo.Content);
        Assert.Equal("image/jpeg", recipe.Photo.ContentType);
    }

    // ──────────── Idempotency: crosswalk prevents duplicate commit ─────────────

    [Fact]
    public async Task CommitAsync_Idempotent_When_Crosswalk_Present_Skips_AuthorRecipe()
    {
        // Simulate a prior run by pre-populating the crosswalk mapping and the recipe repository.
        var h = BuildHarness();
        var existingRecipeId = RecipeId.New();

        // Add the recipe directly to the repo to simulate a prior successful commit.
        var household = HouseholdId.From(HouseholdGuid);
        var existingRecipe = Recipe.Create(household, "Brownies", 4, Clock).Value;
        await h.Recipes.AddAsync(existingRecipe);

        // Write the crosswalk sidecar so the service sees it on the second run.
        var manifestPath = ManifestPath();
        var crosswalkPath = RecipeCrosswalk.ResolvePath(manifestPath);
        var crosswalk = new RecipeCrosswalk
        {
            CommittedAt = DateTimeOffset.UtcNow,
            Mappings    = new Dictionary<string, Guid?> { ["5"] = existingRecipe.Id.Value },
        };
        await crosswalk.WriteAsync(crosswalkPath);

        var row = MakeRow(5, "Brownies", ingredients:
        [
            MakeIngredient(1, ProductAId, UnitId, 200m),
        ]);

        var (results, _) = await h.Service.CommitAsync([row], manifestPath, default);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.False(result.Skipped);
        Assert.Equal(existingRecipe.Id.Value, result.PlantryRecipeId);
        // Recipe must NOT have been added a second time.
        Assert.Single(h.Recipes.Items);

        // Cleanup sidecar
        if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
    }

    // ──────────── Divergence: stale crosswalk entry re-created (plantry-c89g) ──

    [Fact]
    public async Task CommitAsync_StaleCrosswalkEntry_MissingFromDb_ReCreatesRecipe()
    {
        // Regression for plantry-c89g: the crosswalk sidecar maps grocy id 5 to a
        // plantry recipe id, but that recipe does NOT exist in the current DB (e.g. the
        // dev volume was recreated or a prod backup was restored). The commit must NOT
        // trust the sidecar — it must verify against the live DB, find nothing, and
        // re-author the recipe, overwriting the stale mapping.
        var h = BuildHarness();
        var manifestPath = ManifestPath();
        var crosswalkPath = RecipeCrosswalk.ResolvePath(manifestPath);

        // Sidecar points at an id that was never committed to this (fresh) repo.
        var staleId = Guid.CreateVersion7();
        var crosswalk = new RecipeCrosswalk
        {
            CommittedAt = DateTimeOffset.UtcNow,
            Mappings    = new Dictionary<string, Guid?> { ["5"] = staleId },
        };
        await crosswalk.WriteAsync(crosswalkPath);

        var row = MakeRow(5, "Brownies", ingredients:
        [
            MakeIngredient(1, ProductAId, UnitId, 200m),
        ]);

        try
        {
            var (results, _) = await h.Service.CommitAsync([row], manifestPath, default);

            var result = Assert.Single(results);
            Assert.True(result.Success);
            Assert.False(result.Skipped);

            // The recipe was actually created — not a silent no-op.
            var recipe = Assert.Single(h.Recipes.Items);
            Assert.Equal("Brownies", recipe.Name);
            Assert.NotEqual(staleId, recipe.Id.Value);          // a fresh id, not the stale one
            Assert.Equal(recipe.Id.Value, result.PlantryRecipeId);

            // The stale mapping was overwritten with the real, live id.
            var reloaded = await RecipeCrosswalk.TryReadAsync(crosswalkPath);
            Assert.NotNull(reloaded);
            Assert.Equal(recipe.Id.Value, reloaded!.Mappings["5"]);
        }
        finally
        {
            if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
        }
    }

    // ──────────── Idempotency: DuplicateName treated as already committed ──────

    [Fact]
    public async Task CommitAsync_Resolves_DuplicateName_As_Idempotent()
    {
        var h = BuildHarness();

        // The recipe "Brownies" already exists in the household.
        var household = HouseholdId.From(HouseholdGuid);
        var priorRecipe = Recipe.Create(household, "Brownies", 4, Clock).Value;
        await h.Recipes.AddAsync(priorRecipe);

        var row = MakeRow(7, "Brownies", ingredients:
        [
            MakeIngredient(1, ProductAId, UnitId, 100m),
        ]);

        var (results, _) = await h.Service.CommitAsync([row], ManifestPath(), default);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        // Resolved to the pre-existing recipe's id
        Assert.Equal(priorRecipe.Id.Value, result.PlantryRecipeId);
        // Still only one recipe in the repo
        Assert.Single(h.Recipes.Items);
    }

    // ──────────── Group headings round-trip ──────────────────────────────────

    [Fact]
    public async Task CommitAsync_Preserves_GroupHeading_On_Ingredients()
    {
        var h = BuildHarness();

        var row = MakeRow(9, "Caesar Salad", ingredients:
        [
            MakeIngredient(1, ProductAId, UnitId, 100m, groupHeading: "Dressing"),
            MakeIngredient(2, ProductBId, UnitId, 50m,  groupHeading: "Dressing"),
        ]);

        var (results, _) = await h.Service.CommitAsync([row], ManifestPath(), default);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        var recipe = Assert.Single(h.Recipes.Items);
        Assert.All(recipe.Ingredients, ing => Assert.Equal("Dressing", ing.GroupHeading));
    }

    // ──────────── Crosswalk file is written ──────────────────────────────────

    [Fact]
    public async Task CommitAsync_Writes_Crosswalk_Sidecar()
    {
        var h = BuildHarness();
        var manifestPath = ManifestPath();
        var crosswalkPath = RecipeCrosswalk.ResolvePath(manifestPath);

        // Ensure no stale file
        if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);

        var row = MakeRow(42, "Pasta", ingredients:
        [
            MakeIngredient(1, ProductAId, UnitId, 250m),
        ]);

        var (results, returnedPath) = await h.Service.CommitAsync([row], manifestPath, default);

        Assert.Equal(crosswalkPath, returnedPath);
        Assert.True(File.Exists(crosswalkPath));

        var written = await RecipeCrosswalk.TryReadAsync(crosswalkPath);
        Assert.NotNull(written);
        var entry = Assert.Single(written.Mappings);
        Assert.Equal("42", entry.Key);
        Assert.Equal(results[0].PlantryRecipeId!.Value, entry.Value);

        // Cleanup
        if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
    }

    // ──────────── Multiple recipes committed in order ─────────────────────────

    [Fact]
    public async Task CommitAsync_Multiple_Recipes_All_Committed_In_Grocy_Id_Order()
    {
        var h = BuildHarness();

        var rows = new[]
        {
            MakeRow(20, "Bread",  ingredients: [MakeIngredient(1, ProductAId, UnitId, 300m)]),
            MakeRow(10, "Salad",  ingredients: [MakeIngredient(2, ProductBId, UnitId, 150m)]),
            MakeRow(30, "Tacos",  ingredients: [MakeIngredient(3, ProductAId, UnitId, 100m)]),
        };

        var (results, _) = await h.Service.CommitAsync(rows, ManifestPath(), default);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(3, h.Recipes.Items.Count);
        // Results ordered by Grocy id ascending (10, 20, 30)
        Assert.Equal([10, 20, 30], results.Select(r => r.GrocyId));
    }

    // ──────────── Source and directions mapping ───────────────────────────────

    [Fact]
    public async Task CommitAsync_Transfers_Directions_And_Source_To_Recipe()
    {
        var h = BuildHarness();

        var row = MakeRow(11, "Waffles",
            ingredients: [MakeIngredient(1, ProductAId, UnitId, 200m)],
            directions: "Mix batter.\n\nPour into iron.",
            source: "https://example.com/waffles");

        var (results, _) = await h.Service.CommitAsync([row], ManifestPath(), default);

        Assert.Single(results);
        var recipe = Assert.Single(h.Recipes.Items);
        Assert.Equal("Mix batter.\n\nPour into iron.", recipe.Directions);
        Assert.Equal("https://example.com/waffles", recipe.Source);
    }

    // ──────────── Photo idempotency: already present ──────────────────────────

    [Fact]
    public async Task CommitAsync_Photo_AlreadyPresent_Not_Overwritten_When_Same_Length()
    {
        var h = BuildHarness();
        var photoBytes = new byte[] { 1, 2, 3, 4 };

        var row = MakeRow(15, "Muffins",
            ingredients: [MakeIngredient(1, ProductAId, UnitId, 100m)],
            photoBytes: photoBytes,
            photoContentType: "image/png");

        // First commit — AuthorRecipe saves once (body) + CommitPhotoAsync saves once (photo) = 2.
        var (firstResults, _) = await h.Service.CommitAsync([row], ManifestPath(), default);
        Assert.Equal(RecipeCommitService.PhotoCommitDisposition.Committed, firstResults[0].PhotoDisposition);
        Assert.Equal(2, h.Recipes.SaveChangesCalls); // body save + photo save

        // Capture call count after first run.
        var savesBefore = h.Recipes.SaveChangesCalls;

        // Second commit via DuplicateName path — recipe body resolves to existing.
        var (secondResults, _) = await h.Service.CommitAsync([row], ManifestPath(), default);
        // Photo already present (same length) → AlreadyPresent, no extra SaveChanges for photo
        Assert.Equal(RecipeCommitService.PhotoCommitDisposition.AlreadyPresent, secondResults[0].PhotoDisposition);
        Assert.Equal(savesBefore, h.Recipes.SaveChangesCalls); // no new saves
    }

    // ──────────── IsDropped: dropped recipe skipped, null written to crosswalk ─

    [Fact]
    public async Task CommitAsync_DroppedRecipe_SkippedAndNullWrittenToCrosswalk()
    {
        var h = BuildHarness();
        var manifestPath = ManifestPath();
        var crosswalkPath = RecipeCrosswalk.ResolvePath(manifestPath);

        if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);

        var row = MakeRow(55, "Unwanted Recipe",
            ingredients: [MakeIngredient(1, ProductAId, UnitId, 100m)]);
        row.IsDropped = true;

        var (results, _) = await h.Service.CommitAsync([row], manifestPath, default);

        var result = Assert.Single(results);
        Assert.True(result.Skipped);
        Assert.True(result.Success);
        Assert.Null(result.PlantryRecipeId);

        // Not committed to the recipe repo
        Assert.Empty(h.Recipes.Items);

        // Crosswalk written with null sentinel entry
        Assert.True(File.Exists(crosswalkPath));
        var loaded = await RecipeCrosswalk.TryReadAsync(crosswalkPath);
        Assert.NotNull(loaded);
        Assert.True(loaded!.Mappings.ContainsKey("55"));
        Assert.Null(loaded.Mappings["55"]);

        if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
    }

    [Fact]
    public async Task CommitAsync_ReRun_DroppedRecipeInCrosswalk_StaysSkipped()
    {
        // On re-run, a recipe whose crosswalk entry is null stays skipped even
        // if IsDropped is false (the drop decision persists in the crosswalk).
        var h = BuildHarness();
        var manifestPath = ManifestPath();
        var crosswalkPath = RecipeCrosswalk.ResolvePath(manifestPath);

        // Pre-seed a crosswalk with a null entry
        var priorCrosswalk = new RecipeCrosswalk
        {
            CommittedAt = DateTimeOffset.UtcNow,
            Mappings    = new Dictionary<string, Guid?> { ["77"] = null },
        };
        await priorCrosswalk.WriteAsync(crosswalkPath);

        var row = MakeRow(77, "Was Dropped",
            ingredients: [MakeIngredient(1, ProductAId, UnitId, 100m)]);
        // IsDropped = false (user didn't re-check it, but it was dropped in prior run)

        var (results, _) = await h.Service.CommitAsync([row], manifestPath, default);

        var result = Assert.Single(results);
        Assert.True(result.Skipped);
        Assert.True(result.Success);
        Assert.Empty(h.Recipes.Items);

        // Null entry preserved
        var reloaded = await RecipeCrosswalk.TryReadAsync(crosswalkPath);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.Mappings["77"]);

        if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
    }

    // ──────────── Helpers ────────────────────────────────────────────────────

    private static string ManifestPath() =>
        Path.Combine(Path.GetTempPath(), $"plantry-test-{Guid.NewGuid():N}", "manifest.json");

    private static RecipeStagingRow MakeRow(
        int grocyId,
        string name,
        IReadOnlyList<StagedIngredient> ingredients,
        byte[]? photoBytes = null,
        string? photoContentType = null,
        string? directions = null,
        string? source = null)
    {
        return new RecipeStagingRow
        {
            GrocyId        = grocyId,
            GrocyName      = name,
            PlantryName    = name,
            BaseServings   = 4,
            Ingredients    = ingredients,
            PhotoBytes     = photoBytes,
            PhotoContentType = photoContentType,
            Directions     = directions,
            Source         = source,
        };
    }

    private static StagedIngredient MakeIngredient(
        int positionId,
        Guid? plantryProductId,
        Guid? plantryUnitId,
        decimal amount,
        int ordinal = -1,
        string? groupHeading = null)
    {
        return new StagedIngredient
        {
            GrocyPositionId  = positionId,
            GrocyProductId   = positionId * 10, // dummy Grocy id
            GrocyUnitId      = 1,
            Amount           = amount,
            PlantryProductId = plantryProductId,
            PlantryUnitId    = plantryUnitId,
            GroupHeading     = groupHeading,
            Ordinal          = ordinal >= 0 ? ordinal : positionId - 1,
        };
    }
}
