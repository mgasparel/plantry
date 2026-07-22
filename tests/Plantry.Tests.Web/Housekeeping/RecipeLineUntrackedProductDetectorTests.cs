using Plantry.Housekeeping.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Housekeeping;
using Xunit;

namespace Plantry.Tests.Web.Housekeeping;

/// <summary>
/// L2 golden tests for <see cref="RecipeLineUntrackedProductDetector"/> (D7, tidy-up.md §3, redefined) —
/// including the TrackStock boundary (a tracked product never fires) and fingerprint pinning: the
/// fingerprint is the line's product id alone, so re-pointing the same line at a different untracked
/// product must reopen a dismissed finding (§4).
///
/// Tests live in Plantry.Tests.Web because the detector is in Plantry.Composition (referenced
/// transitively via Plantry.Web) — mirrors <c>ShoppingPantryReaderAdapterTests</c>' rationale.
/// </summary>
public sealed class RecipeLineUntrackedProductDetectorTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    private static readonly Guid VanillaId = Guid.Parse("11111111-1111-1111-1111-0000000000d9");
    private static readonly Guid FlourId = Guid.Parse("11111111-1111-1111-1111-0000000000da");
    private static readonly Guid GramId = Guid.Parse("22222222-2222-2222-2222-0000000000dc");

    private static Recipe MakeRecipe(string name, Guid productId, decimal? qty, Guid? unitId, int ordinal = 0)
    {
        var recipe = Recipe.Create(Household, name, defaultServings: 4, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(productId, qty, unitId, GroupHeading: null, Ordinal: ordinal)], Clock);
        return recipe;
    }

    private static RecipeLineUntrackedProductDetector BuildDetector(
        IRecipeRepository recipes, ICatalogProductReader products, ITenantContext? tenant = null) =>
        new(recipes, products, tenant ?? new FakeD7TenantContext(Household.Value));

    [Fact(DisplayName = "Untracked product line, with quantity/unit — produces a finding")]
    public async Task UntrackedProductWithUnit_ProducesFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", VanillaId, 1m, GramId);
        var recipes = new FakeD7RecipeRepository([recipe]);
        var products = new FakeD7CatalogProductReader();
        products.Add(VanillaId, "Vanilla Extract", trackStock: false);

        var detector = BuildDetector(recipes, products);
        var findings = await detector.DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.RecipeLineUntrackedProduct, finding.DetectorId);
        Assert.Equal(recipe.Ingredients[0].Id.Value, finding.SubjectId);
        Assert.Equal("Vanilla Extract", finding.SubjectName);
        Assert.Contains("Sunday Pancakes", finding.Specifics);
        Assert.Equal($"/Recipes/{recipe.Id.Value}/Edit#ingredient-0", finding.FixUrl);
        Assert.Equal("Fix in recipe", finding.FixLabel);
    }

    [Fact(DisplayName = "Untracked staple line with no quantity/unit — still produces a finding (no unit is not required to flag)")]
    public async Task UntrackedStapleLine_NoUnit_StillProducesFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", VanillaId, null, null);
        var recipes = new FakeD7RecipeRepository([recipe]);
        var products = new FakeD7CatalogProductReader();
        products.Add(VanillaId, "Vanilla Extract", trackStock: false);

        var detector = BuildDetector(recipes, products);
        var findings = await detector.DetectAsync();

        Assert.Single(findings);
    }

    [Fact(DisplayName = "Tracked product — never flagged")]
    public async Task TrackedProduct_NoFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId, 200m, GramId);
        var recipes = new FakeD7RecipeRepository([recipe]);
        var products = new FakeD7CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true);

        var detector = BuildDetector(recipes, products);
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "plantry-c7mg: FixUrl anchors on the offending line's own ordinal")]
    public async Task FixUrl_AnchorsOnLineOrdinal()
    {
        var recipe = Recipe.Create(Household, "Sunday Pancakes", defaultServings: 4, Clock).Value;
        recipe.ReplaceIngredients(
            [
                new IngredientLine(FlourId, 200m, GramId, GroupHeading: null, Ordinal: 0),
                new IngredientLine(VanillaId, 1m, GramId, GroupHeading: null, Ordinal: 1),
            ], Clock);
        var recipes = new FakeD7RecipeRepository([recipe]);
        var products = new FakeD7CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true);
        products.Add(VanillaId, "Vanilla Extract", trackStock: false);

        var detector = BuildDetector(recipes, products);
        var findings = await detector.DetectAsync();

        var finding = Assert.Single(findings); // ordinal 0 is tracked — no finding for that line
        Assert.Equal(recipe.Ingredients[1].Id.Value, finding.SubjectId);
        Assert.Equal($"/Recipes/{recipe.Id.Value}/Edit#ingredient-1", finding.FixUrl);
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var recipe = MakeRecipe("Sunday Pancakes", VanillaId, 1m, GramId);
        var recipes = new FakeD7RecipeRepository([recipe]);
        var products = new FakeD7CatalogProductReader();
        products.Add(VanillaId, "Vanilla Extract", trackStock: false);

        var detector = BuildDetector(recipes, products, new FakeD7TenantContext(null));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Fingerprint pinning: the same product on a different recipe produces the same fingerprint")]
    public async Task Fingerprint_SameProduct_SameFingerprint_AcrossRecipes()
    {
        var recipeA = MakeRecipe("Sunday Pancakes", VanillaId, 1m, GramId);
        var recipeB = MakeRecipe("Banana Bread", VanillaId, 2m, GramId);
        var recipes = new FakeD7RecipeRepository([recipeA, recipeB]);
        var products = new FakeD7CatalogProductReader();
        products.Add(VanillaId, "Vanilla Extract", trackStock: false);

        var findings = await BuildDetector(recipes, products).DetectAsync();

        Assert.Equal(2, findings.Count);
        Assert.Equal(findings[0].FactsFingerprint, findings[1].FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: re-pointing the line at a different untracked product changes the fingerprint")]
    public async Task Fingerprint_ChangesWithDifferentProduct()
    {
        var sugarId = Guid.Parse("11111111-1111-1111-1111-0000000000db");

        var recipeVanilla = MakeRecipe("Sunday Pancakes", VanillaId, 1m, GramId);
        var recipesVanilla = new FakeD7RecipeRepository([recipeVanilla]);
        var productsVanilla = new FakeD7CatalogProductReader();
        productsVanilla.Add(VanillaId, "Vanilla Extract", trackStock: false);
        var findingVanilla = Assert.Single(await BuildDetector(recipesVanilla, productsVanilla).DetectAsync());

        var recipeSugar = MakeRecipe("Sunday Pancakes", sugarId, 1m, GramId);
        var recipesSugar = new FakeD7RecipeRepository([recipeSugar]);
        var productsSugar = new FakeD7CatalogProductReader();
        productsSugar.Add(sugarId, "Powdered Sugar", trackStock: false);
        var findingSugar = Assert.Single(await BuildDetector(recipesSugar, productsSugar).DetectAsync());

        Assert.NotEqual(findingVanilla.FactsFingerprint, findingSugar.FactsFingerprint);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class FakeD7TenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakeD7RecipeRepository(IReadOnlyList<Recipe> recipes) : IRecipeRepository
{
    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        Task.FromResult(recipes);

    public Task AddAsync(Recipe recipe, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
        Task.FromResult(recipes.SingleOrDefault(r => r.Id == id));
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(recipes.Count > 0);
    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);
    public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

file sealed class FakeD7CatalogProductReader : ICatalogProductReader
{
    private readonly Dictionary<Guid, CatalogProduct> _products = [];

    public void Add(Guid id, string name, bool trackStock) =>
        _products[id] = new CatalogProduct(id, name, trackStock, Guid.NewGuid(), ParentProductId: null, IsParent: false, VariantProductIds: []);

    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.TryGetValue(productId, out var p) ? p : null);

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();
}
