using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// Unit tests for <see cref="RecipeExpansionService"/> — the D4 expansion choke point
/// (recipe-composition.md §4).
///
/// Covers:
/// <list type="bullet">
///   <item>Direct-only recipe → every line has an empty path, quantities unchanged.</item>
///   <item>One inclusion → factor f = S/D applied to the sub's quantities.</item>
///   <item>Nested inclusion → factors multiply along the path.</item>
///   <item>Duplicate sub → two distinct paths, aggregated by nothing here (D14).</item>
///   <item>Inclusion-only recipe → no direct lines, only expanded sub lines.</item>
///   <item>Untracked staple (null qty/unit) → passes through untouched.</item>
///   <item>3-dp rounding of scaled quantities.</item>
///   <item>(Path, IngredientId) unique per line.</item>
///   <item>Defensive visited-set → a cyclic graph terminates with an error, not a hang.</item>
/// </list>
/// </summary>
public sealed class RecipeExpansionServiceTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    private static readonly Guid UnitG = Guid.CreateVersion7();

    private static (RecipeExpansionService Service, FakeRecipeRepository Repo) Build()
    {
        var repo = new FakeRecipeRepository();
        return (new RecipeExpansionService(repo), repo);
    }

    /// <summary>Creates a recipe with the given ingredient/inclusion lines and adds it to the repo.</summary>
    private static Recipe Seed(
        FakeRecipeRepository repo,
        string name,
        int defaultServings,
        IReadOnlyList<IngredientLine> ingredients,
        IReadOnlyList<InclusionLine>? inclusions = null)
    {
        var recipe = Recipe.Create(Household, name, defaultServings, Clock).Value;
        var result = recipe.ReplaceLines(ingredients, inclusions ?? [], Clock);
        Assert.True(result.IsSuccess, $"Seed failed: {result.Error.Code}");
        repo.Items.Add(recipe);
        return recipe;
    }

    private static IngredientLine Ing(decimal? qty, Guid? unit, string? group = null, int ordinal = 0, Guid? product = null) =>
        new(product ?? Guid.CreateVersion7(), qty, unit, group, ordinal);

    // ── Direct-only ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DirectOnly_All_Lines_Have_Empty_Path_And_Unchanged_Quantities()
    {
        var (service, repo) = Build();
        var recipe = Seed(repo, "Guacamole", 2,
        [
            Ing(3m, UnitG, ordinal: 0),
            Ing(1m, UnitG, ordinal: 1),
        ]);

        var result = await service.ExpandAsync(recipe.Id);

        Assert.True(result.IsSuccess);
        var lines = result.Value;
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Empty(l.Path));
        Assert.All(lines, l => Assert.Equal(string.Empty, l.PathKey));
        Assert.All(lines, l => Assert.Equal(recipe.Id, l.SourceRecipeId));
        // Quantities untouched (factor = 1) and ingredient ids match the recipe's own lines.
        Assert.Equal(new[] { 3m, 1m }, lines.Select(l => l.Quantity!.Value).ToArray());
        Assert.Equal(
            recipe.Ingredients.Select(i => i.Id).OrderBy(i => i.Value).ToArray(),
            lines.Select(l => l.IngredientId).OrderBy(i => i.Value).ToArray());
    }

    // ── One inclusion (factor) ──────────────────────────────────────────────────

    [Fact]
    public async Task OneInclusion_Applies_Factor_And_Sets_Provenance()
    {
        var (service, repo) = Build();
        // Sub: default 2 servings, one ingredient of 100.
        var sub = Seed(repo, "Nacho Cheese", 2, [Ing(100m, UnitG, ordinal: 0)]);
        // Parent: no direct ingredients, includes 3 servings of the sub → f = 3/2 = 1.5.
        var parent = Seed(repo, "Nachos", 4, [], [new InclusionLine(sub.Id, 3m, null, 0)]);

        var result = await service.ExpandAsync(parent.Id);

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Value);
        Assert.Equal(150m, line.Quantity);                 // 100 × 1.5
        Assert.Equal(sub.Ingredients[0].Id, line.IngredientId);
        Assert.Equal(sub.Id, line.SourceRecipeId);          // provenance = the sub, not the parent
        Assert.Single(line.Path);
        Assert.Equal(parent.Inclusions[0].Id, line.Path[0]);
        Assert.Equal(parent.Inclusions[0].Id.Value.ToString(), line.PathKey);
        // GroupPath carries the inclusion display name (the sub's name).
        Assert.Equal(new[] { "Nacho Cheese" }, line.GroupPath.ToArray());
    }

    // ── Nested inclusion (factors multiply) ─────────────────────────────────────

    [Fact]
    public async Task NestedInclusion_Factors_Multiply_Along_Path()
    {
        var (service, repo) = Build();
        // Leaf: default 2, ingredient 10.
        var leaf = Seed(repo, "Cashew Cream", 2, [Ing(10m, UnitG, ordinal: 0)]);
        // Mid: default 5, includes 4 servings of leaf → f_leaf-in-mid = 4/2 = 2.
        var mid = Seed(repo, "Cheese Sauce", 5, [], [new InclusionLine(leaf.Id, 4m, null, 0)]);
        // Top: default 3, includes 15 servings of mid → f_mid-in-top = 15/5 = 3.
        var top = Seed(repo, "Loaded Nachos", 3, [], [new InclusionLine(mid.Id, 15m, null, 0)]);

        var result = await service.ExpandAsync(top.Id);

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Value);
        // 10 × 3 × 2 = 60.
        Assert.Equal(60m, line.Quantity);
        Assert.Equal(leaf.Id, line.SourceRecipeId);
        // Path is the two inclusion ids, root→…→leaf.
        Assert.Equal(2, line.Path.Count);
        Assert.Equal(top.Inclusions[0].Id, line.Path[0]);
        Assert.Equal(mid.Inclusions[0].Id, line.Path[1]);
        Assert.Equal($"{top.Inclusions[0].Id.Value}/{mid.Inclusions[0].Id.Value}", line.PathKey);
        // GroupPath is the nested inclusion display names.
        Assert.Equal(new[] { "Cheese Sauce", "Cashew Cream" }, line.GroupPath.ToArray());
    }

    // ── Duplicate sub (two distinct paths) ──────────────────────────────────────

    [Fact]
    public async Task DuplicateSub_Yields_Two_Distinct_Paths()
    {
        var (service, repo) = Build();
        var sub = Seed(repo, "Pie Crust", 2, [Ing(200m, UnitG, ordinal: 0)]);
        // Parent includes the same sub twice — "Base" ×1 (f=1/2) and "Lattice" ×0.5 (f=0.25) (D14).
        var parent = Seed(repo, "Apple Pie", 4, [],
        [
            new InclusionLine(sub.Id, 1m, "Base", 0),
            new InclusionLine(sub.Id, 0.5m, "Lattice", 1),
        ]);

        var result = await service.ExpandAsync(parent.Id);

        Assert.True(result.IsSuccess);
        var lines = result.Value;
        Assert.Equal(2, lines.Count);
        // Same ingredient id (one sub), two DISTINCT paths.
        Assert.All(lines, l => Assert.Equal(sub.Ingredients[0].Id, l.IngredientId));
        Assert.Equal(2, lines.Select(l => l.PathKey).Distinct().Count());
        Assert.Equal(parent.Inclusions[0].Id, lines[0].Path[0]);
        Assert.Equal(parent.Inclusions[1].Id, lines[1].Path[0]);
        // Quantities scale independently: 200×0.5 = 100 and 200×0.25 = 50.
        Assert.Equal(100m, lines[0].Quantity);
        Assert.Equal(50m, lines[1].Quantity);
    }

    // ── Inclusion-only recipe ───────────────────────────────────────────────────

    [Fact]
    public async Task InclusionOnly_Recipe_Emits_Only_Sub_Lines()
    {
        var (service, repo) = Build();
        var sub = Seed(repo, "Caesar Base", 2, [Ing(50m, UnitG, ordinal: 0), Ing(20m, UnitG, ordinal: 1)]);
        var parent = Seed(repo, "Caesar Deluxe", 2, [], [new InclusionLine(sub.Id, 2m, null, 0)]);

        var result = await service.ExpandAsync(parent.Id);

        Assert.True(result.IsSuccess);
        var lines = result.Value;
        Assert.Equal(2, lines.Count);
        // No direct (empty-path) lines — the parent contributes none.
        Assert.DoesNotContain(lines, l => l.Path.Count == 0);
        Assert.All(lines, l => Assert.Equal(sub.Id, l.SourceRecipeId));
    }

    // ── Untracked staple pass-through ───────────────────────────────────────────

    [Fact]
    public async Task UntrackedStaple_Null_Quantity_Passes_Through_Even_Under_A_Factor()
    {
        var (service, repo) = Build();
        // Sub with a tracked line AND an untracked staple ("to taste": null qty/unit).
        var sub = Seed(repo, "Marinara", 4,
        [
            Ing(400m, UnitG, ordinal: 0),
            Ing(null, null, ordinal: 1),   // untracked staple
        ]);
        var parent = Seed(repo, "Pasta Bake", 2, [], [new InclusionLine(sub.Id, 8m, null, 0)]); // f = 8/4 = 2

        var result = await service.ExpandAsync(parent.Id);

        Assert.True(result.IsSuccess);
        var lines = result.Value;
        var staple = Assert.Single(lines, l => l.UnitId is null);
        Assert.Null(staple.Quantity);          // stays null despite factor 2
        var tracked = Assert.Single(lines, l => l.UnitId is not null);
        Assert.Equal(800m, tracked.Quantity);  // 400 × 2
    }

    // ── 3-dp rounding ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Scaled_Quantity_Is_Rounded_To_Three_Decimal_Places()
    {
        var (service, repo) = Build();
        // Sub default 3, ingredient 10; parent includes 1 serving → f = 1/3 = 0.333…, qty = 3.333…
        var sub = Seed(repo, "Thirds", 3, [Ing(10m, UnitG, ordinal: 0)]);
        var parent = Seed(repo, "One Third", 1, [], [new InclusionLine(sub.Id, 1m, null, 0)]);

        var result = await service.ExpandAsync(parent.Id);

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Value);
        Assert.Equal(3.333m, line.Quantity);   // Math.Round(10 × 1/3, 3)
    }

    // ── (Path, IngredientId) uniqueness ─────────────────────────────────────────

    [Fact]
    public async Task Path_And_IngredientId_Are_Unique_Per_Line()
    {
        var (service, repo) = Build();
        var sub = Seed(repo, "Base", 2, [Ing(10m, UnitG, ordinal: 0), Ing(5m, UnitG, ordinal: 1)]);
        var parent = Seed(repo, "Dish", 2,
            [Ing(1m, UnitG, ordinal: 0)],
            [
                new InclusionLine(sub.Id, 2m, null, 1),
                new InclusionLine(sub.Id, 4m, null, 2),
            ]);

        var result = await service.ExpandAsync(parent.Id);

        Assert.True(result.IsSuccess);
        var lines = result.Value;
        // 1 direct + 2 sub-ingredients × 2 inclusions = 5 lines.
        Assert.Equal(5, lines.Count);
        var keys = lines.Select(l => (l.PathKey, l.IngredientId)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        // Exactly one direct (empty-path) line.
        Assert.Single(lines, l => l.Path.Count == 0);
    }

    // ── Defensive visited-set (cyclic graph) ────────────────────────────────────

    [Fact]
    public async Task CyclicGraph_Bypassing_N4_Terminates_With_An_Error()
    {
        var (service, repo) = Build();
        // Build A and B each with a direct ingredient, then wire A→B and B→A directly through the
        // aggregate (ReplaceLines enforces N1/N2/N3 but NOT the cross-aggregate N4 DAG check), so the
        // in-memory graph is cyclic — exactly the state N4 prevents at save.
        var a = Seed(repo, "A", 2, [Ing(1m, UnitG, ordinal: 0)]);
        var b = Seed(repo, "B", 2, [Ing(1m, UnitG, ordinal: 0)]);
        a.ReplaceLines([Ing(1m, UnitG, ordinal: 0)], [new InclusionLine(b.Id, 1m, null, 1)], Clock);
        b.ReplaceLines([Ing(1m, UnitG, ordinal: 0)], [new InclusionLine(a.Id, 1m, null, 1)], Clock);

        var result = await service.ExpandAsync(a.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.ExpansionCycle", result.Error.Code);
    }

    [Fact]
    public async Task Missing_Root_Recipe_Returns_NotFound()
    {
        var (service, _) = Build();

        var result = await service.ExpandAsync(RecipeId.New());

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    // ── Resolver parity: repo path vs batched map path (Browse Option B — plantry-ckzc) ──────────

    [Fact]
    public async Task RepoPath_And_MapPath_Produce_Identical_Expansion()
    {
        // A three-level tree with direct ingredients at every level, so factor accumulation, ordering, and
        // provenance are all exercised. The batched map path (Browse Option B) must produce byte-for-byte the
        // same expansion as the repo path — they share the single recursive core, only the id→Recipe source
        // differs — so Browse's badges cannot drift from the Details-page (repo) figures.
        var (service, repo) = Build();
        var leaf = Seed(repo, "Cashew Cream", 2, [Ing(10m, UnitG, ordinal: 0)]);
        var mid = Seed(repo, "Cheese Sauce", 5, [Ing(7m, UnitG, ordinal: 0)], [new InclusionLine(leaf.Id, 4m, null, 1)]);
        var top = Seed(repo, "Loaded Nachos", 3, [Ing(2m, UnitG, ordinal: 0)], [new InclusionLine(mid.Id, 15m, null, 1)]);

        var viaRepo = await service.ExpandAsync(top.Id);
        var map = repo.Items.ToDictionary(r => r.Id);
        var viaMap = await service.ExpandAsync(top.Id, map);

        Assert.True(viaRepo.IsSuccess);
        Assert.True(viaMap.IsSuccess);
        Assert.Equal(
            viaRepo.Value.Select(l => (l.PathKey, l.IngredientId, l.SourceRecipeId, l.ProductId, l.Quantity, l.UnitId, string.Join('|', l.GroupPath))).ToList(),
            viaMap.Value.Select(l => (l.PathKey, l.IngredientId, l.SourceRecipeId, l.ProductId, l.Quantity, l.UnitId, string.Join('|', l.GroupPath))).ToList());
    }

    [Fact]
    public async Task MapPath_Missing_Sub_Returns_ExpansionSubNotFound()
    {
        // The map omits a legitimately-referenced sub (models a dangling/archived inclusion absent from the
        // non-archived Browse set). The resolver returns null and expansion fails — the CALLER degrades that
        // one row to flat, but the service itself surfaces the well-known error.
        var (service, repo) = Build();
        var sub = Seed(repo, "Sub", 2, [Ing(10m, UnitG, ordinal: 0)]);
        var parent = Seed(repo, "Parent", 2, [], [new InclusionLine(sub.Id, 2m, null, 0)]);

        var map = new Dictionary<RecipeId, Recipe> { [parent.Id] = parent }; // sub deliberately absent

        var result = await service.ExpandAsync(parent.Id, map);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.ExpansionSubNotFound", result.Error.Code);
    }

    [Fact]
    public async Task MapPath_Missing_Root_Returns_NotFound()
    {
        var (service, _) = Build();

        var result = await service.ExpandAsync(RecipeId.New(), new Dictionary<RecipeId, Recipe>());

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }
}
