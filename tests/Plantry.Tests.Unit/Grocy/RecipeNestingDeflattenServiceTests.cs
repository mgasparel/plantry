using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Recipes.Application;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="RecipeNestingDeflattenService"/> — re-importing Grocy nesting edges as
/// recipe inclusions (recipe-composition.md §10 / D16, plantry-fqb0.8). Each test drives the real
/// staging + commit pipeline (<see cref="RecipeStager"/> + <see cref="RecipeCommitService"/>) from a
/// manifest fixture to establish the committed state, then runs the de-flatten pass and asserts the
/// disposition + resulting aggregate.
///
/// Covers the acceptance criteria:
/// - Untouched-since-import parent converts (flattened lines → direct ingredients + inclusion; servings = nesting amount).
/// - Edited parent skipped + reported; no mutation.
/// - Edge with an uncommitted/missing sub skipped + reported.
/// - Re-run is idempotent (already-converted; no duplicate inclusions).
/// - Summary exposes converted/skipped counts.
/// </summary>
public sealed class RecipeNestingDeflattenServiceTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly IClock Clock = SystemClock.Instance;

    // Plantry (post-crosswalk) ids. UnitId is the default unit of every product so no conversion is needed.
    private static readonly Guid DirectProductId = Guid.CreateVersion7(); // parent's own ingredient
    private static readonly Guid SubProductId    = Guid.CreateVersion7(); // sub-recipe's ingredient
    private static readonly Guid UnitId          = Guid.CreateVersion7();

    // Grocy ids used in the manifest fixtures.
    private const int ParentGrocyId       = 1;
    private const int SubGrocyId          = 2;
    private const int GrocyDirectProductId = 10;
    private const int GrocySubProductId    = 20;
    private const int GrocyUnitId          = 1;

    // ──────────── Test harness ───────────────────────────────────────────────

    private sealed class Harness
    {
        public FakeRecipeRepository Recipes { get; }
        public FakeCatalogProductReader Products { get; }
        public RecipeCommitService CommitService { get; }
        public RecipeNestingDeflattenService DeflattenService { get; }

        public Harness()
        {
            Recipes = new FakeRecipeRepository();
            var tags      = new FakeTagRepository();
            Products = new FakeCatalogProductReader();
            var converter = new FakeUnitConverter();
            var writer    = new FakeCatalogWriter(Products, converter);
            var tenant    = new FakeTenantContext(HouseholdGuid);
            var author    = new AuthorRecipe(Recipes, tags, Products, writer, converter, Clock, tenant, NullLogger<AuthorRecipe>.Instance);

            CommitService    = new RecipeCommitService(author, Recipes, Clock, tenant);
            DeflattenService = new RecipeNestingDeflattenService(author, Recipes, tenant);

            // Both products default to UnitId so committed ingredient unit == product default (no conversion).
            Products.Register(new CatalogProduct(DirectProductId, "Flour", TrackStock: true, UnitId, null, false, []));
            Products.Register(new CatalogProduct(SubProductId,    "Sauce", TrackStock: true, UnitId, null, false, []));
        }
    }

    // ──────────── Manifest fixture ────────────────────────────────────────────

    /// <summary>
    /// A parent recipe (Grocy id 1, base 4 servings) with one direct ingredient plus a nesting edge to a
    /// sub-recipe (Grocy id 2, base 4 servings) that has one ingredient. Nesting amount = 2 servings, so the
    /// flatten scales the sub ingredient by 2/4 = 0.5 while the INCLUSION servings stay 2 (the raw amount).
    /// </summary>
    private static GrocyManifest BuildManifest(bool includeSubRecipe = true, decimal nestingServings = 2m)
    {
        var recipes = new List<GrocyRecipe>
        {
            new(ParentGrocyId, "Caesar Salad", null, 4, null, "normal", null, null, null),
        };
        if (includeSubRecipe)
            recipes.Add(new GrocyRecipe(SubGrocyId, "Caesar Dressing", null, 4, null, "normal", null, null, null));

        var positions = new List<GrocyRecipePosition>
        {
            // Parent direct ingredient — amount 200
            new(101, ParentGrocyId, GrocyDirectProductId, 200m, GrocyUnitId, null, null, null, null, null, null, null),
        };
        if (includeSubRecipe)
            positions.Add(new GrocyRecipePosition(
                201, SubGrocyId, GrocySubProductId, 100m, GrocyUnitId, null, null, null, null, null, null, null));

        var nestings = new List<GrocyRecipeNesting>
        {
            new(301, ParentGrocyId, SubGrocyId, nestingServings, null),
        };

        return new GrocyManifest
        {
            ExtractedAt     = DateTimeOffset.UtcNow,
            Recipes         = recipes,
            RecipePositions = positions,
            RecipeNestings  = nestings,
            Products =
            [
                new(GrocyDirectProductId, "Flour", null, null, GrocyUnitId, GrocyUnitId, null, null, null, null, null, null, null, null, null, null, null, null, null),
                new(GrocySubProductId,    "Sauce", null, null, GrocyUnitId, GrocyUnitId, null, null, null, null, null, null, null, null, null, null, null, null, null),
            ],
            QuantityUnits = [new(GrocyUnitId, "g", null, null)],
        };
    }

    private static string NewManifestPath() =>
        Path.Combine(Path.GetTempPath(), $"plantry-deflatten-test-{Guid.NewGuid():N}", "manifest.json");

    /// <summary>Stages the manifest with the given product crosswalk and commits — establishing committed state.</summary>
    private static async Task<IReadOnlyList<RecipeStagingRow>> StageAndCommitAsync(
        Harness h, GrocyManifest manifest, string manifestPath, IReadOnlyDictionary<int, Guid> productMap)
    {
        var unitMap = new Dictionary<int, Guid> { [GrocyUnitId] = UnitId };
        var productIdToName = manifest.Products.ToDictionary(p => p.Id, p => p.Name);
        var unitIdToName    = manifest.QuantityUnits.ToDictionary(u => u.Id, u => u.Name);

        var rows = RecipeStager.Stage(manifest, productMap, productIdToName, unitMap, unitIdToName, existingRecipeNames: null);
        await h.CommitService.CommitAsync(rows, manifestPath, default);
        return rows;
    }

    private static Dictionary<int, Guid> FullProductMap() => new()
    {
        [GrocyDirectProductId] = DirectProductId,
        [GrocySubProductId]    = SubProductId,
    };

    private static void Cleanup(string manifestPath)
    {
        var crosswalkPath = RecipeCrosswalk.ResolvePath(manifestPath);
        if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
    }

    // ──────────── AC 1: untouched parent converts ─────────────────────────────

    [Fact]
    public async Task Deflatten_UntouchedParent_ReplacesFlattenedLines_WithDirectPlusInclusion()
    {
        var h = new Harness();
        var manifest = BuildManifest();
        var manifestPath = NewManifestPath();

        var rows = await StageAndCommitAsync(h, manifest, manifestPath, FullProductMap());

        // Sanity: parent committed with a flattened sub ingredient (scaled 100 * 2/4 = 50) + direct.
        var parentBefore = h.Recipes.Items.Single(r => r.Name == "Caesar Salad");
        Assert.Equal(2, parentBefore.Ingredients.Count);
        Assert.Empty(parentBefore.Inclusions);
        var subId = h.Recipes.Items.Single(r => r.Name == "Caesar Dressing").Id;

        var summary = await h.DeflattenService.DeflattenAsync(rows, manifest, manifestPath, default);

        var result = Assert.Single(summary.Results);
        Assert.Equal(RecipeNestingDeflattenService.DeflattenDisposition.Converted, result.Disposition);
        Assert.Equal(1, summary.Converted);
        Assert.Equal(0, summary.Skipped);

        var parentAfter = h.Recipes.Items.Single(r => r.Name == "Caesar Salad");
        // Only the direct ingredient remains; the flattened sub ingredient is gone.
        var ingredient = Assert.Single(parentAfter.Ingredients);
        Assert.Equal(DirectProductId, ingredient.ProductId);
        Assert.Equal(200m, ingredient.Quantity);
        // One inclusion, servings = the raw Grocy nesting amount (2), NOT the scaled flatten quantity.
        var inclusion = Assert.Single(parentAfter.Inclusions);
        Assert.Equal(subId, inclusion.SubRecipeId);
        Assert.Equal(2m, inclusion.Servings);

        Cleanup(manifestPath);
    }

    // ──────────── AC 2: edited parent skipped ─────────────────────────────────

    [Fact]
    public async Task Deflatten_EditedParent_SkippedAndReported_NoMutation()
    {
        var h = new Harness();
        var manifest = BuildManifest();
        var manifestPath = NewManifestPath();

        var rows = await StageAndCommitAsync(h, manifest, manifestPath, FullProductMap());

        // Simulate a user edit since import: change the direct ingredient's quantity.
        var parent = h.Recipes.Items.Single(r => r.Name == "Caesar Salad");
        var edited = parent.Ingredients
            .Select(i => new IngredientLine(i.ProductId, i.Quantity + 5m, i.UnitId, i.GroupHeading, i.Ordinal))
            .ToList();
        Assert.True(parent.ReplaceLines(edited, [], Clock).IsSuccess);

        var summary = await h.DeflattenService.DeflattenAsync(rows, manifest, manifestPath, default);

        var result = Assert.Single(summary.Results);
        Assert.Equal(RecipeNestingDeflattenService.DeflattenDisposition.SkippedEdited, result.Disposition);
        Assert.NotNull(result.Note);
        Assert.Equal(0, summary.Converted);
        Assert.Equal(1, summary.SkippedEdited);

        // No inclusion was added — the edited recipe is untouched by de-flatten.
        var parentAfter = h.Recipes.Items.Single(r => r.Name == "Caesar Salad");
        Assert.Empty(parentAfter.Inclusions);
        Assert.Equal(2, parentAfter.Ingredients.Count);

        Cleanup(manifestPath);
    }

    // ──────────── AC 3: uncommitted / missing sub skipped ─────────────────────

    [Fact]
    public async Task Deflatten_MissingSub_SkippedAndReported_NoMutation()
    {
        var h = new Harness();
        var manifest = BuildManifest();
        var manifestPath = NewManifestPath();

        // Commit with the SUB product crosswalk-missing → the sub recipe has no committable ingredient and is
        // skipped at commit, so it never enters the recipe crosswalk. The nesting edge therefore points at an
        // uncommitted sub.
        var productMap = new Dictionary<int, Guid> { [GrocyDirectProductId] = DirectProductId };
        var rows = await StageAndCommitAsync(h, manifest, manifestPath, productMap);

        // Parent committed (its direct ingredient resolved); sub did not.
        Assert.Contains(h.Recipes.Items, r => r.Name == "Caesar Salad");
        Assert.DoesNotContain(h.Recipes.Items, r => r.Name == "Caesar Dressing");

        var summary = await h.DeflattenService.DeflattenAsync(rows, manifest, manifestPath, default);

        var result = Assert.Single(summary.Results);
        Assert.Equal(RecipeNestingDeflattenService.DeflattenDisposition.SkippedMissingSub, result.Disposition);
        Assert.Equal(1, summary.SkippedMissingSub);
        Assert.Equal(0, summary.Converted);

        var parentAfter = h.Recipes.Items.Single(r => r.Name == "Caesar Salad");
        Assert.Empty(parentAfter.Inclusions);

        Cleanup(manifestPath);
    }

    // ──────────── AC 4: idempotent re-run ─────────────────────────────────────

    [Fact]
    public async Task Deflatten_ReRun_ReportsAlreadyConverted_NoDuplicateInclusions()
    {
        var h = new Harness();
        var manifest = BuildManifest();
        var manifestPath = NewManifestPath();

        var rows = await StageAndCommitAsync(h, manifest, manifestPath, FullProductMap());

        // First pass converts.
        var first = await h.DeflattenService.DeflattenAsync(rows, manifest, manifestPath, default);
        Assert.Equal(1, first.Converted);
        var parentAfterFirst = h.Recipes.Items.Single(r => r.Name == "Caesar Salad");
        Assert.Single(parentAfterFirst.Inclusions);

        // Second pass finds the inclusion already present → already-converted, no mutation.
        var second = await h.DeflattenService.DeflattenAsync(rows, manifest, manifestPath, default);

        var result = Assert.Single(second.Results);
        Assert.Equal(RecipeNestingDeflattenService.DeflattenDisposition.AlreadyConverted, result.Disposition);
        Assert.Equal(1, second.AlreadyConverted);
        Assert.Equal(0, second.Converted);

        // Still exactly one inclusion — no duplicate.
        var parentAfterSecond = h.Recipes.Items.Single(r => r.Name == "Caesar Salad");
        Assert.Single(parentAfterSecond.Inclusions);

        Cleanup(manifestPath);
    }

    // ──────────── Parent not committed (dropped / all-missing) skipped ─────────

    [Fact]
    public async Task Deflatten_ParentNotInCrosswalk_SkippedParentNotCommitted()
    {
        var h = new Harness();
        var manifest = BuildManifest();
        var manifestPath = NewManifestPath();

        // Commit with NEITHER product resolvable → parent has no committable ingredient and is skipped;
        // it never enters the crosswalk. (Sub also skipped, but the parent gate short-circuits first.)
        var rows = await StageAndCommitAsync(h, manifest, manifestPath, new Dictionary<int, Guid>());

        Assert.Empty(h.Recipes.Items);

        var summary = await h.DeflattenService.DeflattenAsync(rows, manifest, manifestPath, default);

        var result = Assert.Single(summary.Results);
        Assert.Equal(RecipeNestingDeflattenService.DeflattenDisposition.SkippedParentNotCommitted, result.Disposition);
        Assert.Equal(1, summary.Skipped);

        Cleanup(manifestPath);
    }

    // ──────────── No crosswalk at all → every parent reported not-committed ────

    [Fact]
    public async Task Deflatten_NoCrosswalk_ReportsParentNotCommitted()
    {
        var h = new Harness();
        var manifest = BuildManifest();
        var manifestPath = NewManifestPath();

        // Stage only — never commit, so no crosswalk sidecar exists.
        var unitMap = new Dictionary<int, Guid> { [GrocyUnitId] = UnitId };
        var rows = RecipeStager.Stage(
            manifest, FullProductMap(),
            manifest.Products.ToDictionary(p => p.Id, p => p.Name),
            unitMap,
            manifest.QuantityUnits.ToDictionary(u => u.Id, u => u.Name),
            existingRecipeNames: null);

        var summary = await h.DeflattenService.DeflattenAsync(rows, manifest, manifestPath, default);

        var result = Assert.Single(summary.Results);
        Assert.Equal(RecipeNestingDeflattenService.DeflattenDisposition.SkippedParentNotCommitted, result.Disposition);
    }
}
