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
/// L2 golden tests for <see cref="RecipeConversionGapDetector"/> (D2, tidy-up.md §3) — including
/// fingerprint pinning: the fingerprint covers only the line's authored unit + the product's default
/// unit, never the quantity (§4).
///
/// Tests live in Plantry.Tests.Web because the detector is in Plantry.Composition (referenced
/// transitively via Plantry.Web) — mirrors <c>ShoppingPantryReaderAdapterTests</c>' rationale.
/// </summary>
public sealed class RecipeConversionGapDetectorTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    private static readonly Guid FlourId = Guid.Parse("11111111-1111-1111-1111-0000000000d2");
    private static readonly Guid GramId = Guid.Parse("22222222-2222-2222-2222-0000000000d4");
    private static readonly Guid CupId = Guid.Parse("22222222-2222-2222-2222-0000000000d5");

    private static Recipe MakeRecipe(string name, Guid productId, decimal? qty, Guid? unitId)
    {
        var recipe = Recipe.Create(Household, name, defaultServings: 4, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(productId, qty, unitId, GroupHeading: null, Ordinal: 0)], Clock);
        return recipe;
    }

    private static RecipeConversionGapDetector BuildDetector(
        IRecipeRepository recipes, ICatalogProductReader products, IUnitConverter converter,
        ITenantContext? tenant = null) =>
        new(recipes, products, converter, tenant ?? new FakeD2TenantContext(Household.Value));

    [Fact(DisplayName = "Tracked line, unit differs from default, no conversion path — produces a finding")]
    public async Task TrackedLineNoConversionPath_ProducesFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId, 2m, CupId);
        var recipes = new FakeD2RecipeRepository([recipe]);
        var products = new FakeD2CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true, GramId);

        var detector = BuildDetector(recipes, products, new FakeD2UnitConverter(succeeds: false));
        var findings = await detector.DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.RecipeConversionGap, finding.DetectorId);
        Assert.Equal(recipe.Ingredients[0].Id.Value, finding.SubjectId);
        Assert.Equal("All-Purpose Flour", finding.SubjectName);
        Assert.Contains("Sunday Pancakes", finding.Specifics);
        Assert.Equal($"/Recipes/{recipe.Id.Value}/Edit", finding.FixUrl);
    }

    [Fact(DisplayName = "A conversion path exists — no finding")]
    public async Task ConversionPathExists_NoFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId, 2m, CupId);
        var recipes = new FakeD2RecipeRepository([recipe]);
        var products = new FakeD2CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true, GramId);

        var detector = BuildDetector(recipes, products, new FakeD2UnitConverter(succeeds: true));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Line unit equals the product's own default unit — no conversion needed, no finding")]
    public async Task LineUnitEqualsDefaultUnit_NoFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId, 200m, GramId); // already in the default unit
        var recipes = new FakeD2RecipeRepository([recipe]);
        var products = new FakeD2CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true, GramId);

        var detector = BuildDetector(recipes, products, new FakeD2UnitConverter(succeeds: false));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Untracked product — never flagged (cooking never deducts it, R7)")]
    public async Task UntrackedProduct_NoFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId, 1m, CupId);
        var recipes = new FakeD2RecipeRepository([recipe]);
        var products = new FakeD2CatalogProductReader();
        products.Add(FlourId, "Vanilla Extract", trackStock: false, GramId);

        var detector = BuildDetector(recipes, products, new FakeD2UnitConverter(succeeds: false));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Untracked staple line (no unit) — never flagged")]
    public async Task UntrackedStapleLine_NoUnit_NoFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId, null, null);
        var recipes = new FakeD2RecipeRepository([recipe]);
        var products = new FakeD2CatalogProductReader();
        products.Add(FlourId, "Salt", trackStock: true, GramId);

        var detector = BuildDetector(recipes, products, new FakeD2UnitConverter(succeeds: false));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Fingerprint pinning: identical unit pair on two different recipes produces the same fingerprint")]
    public async Task Fingerprint_SameUnitPair_SameFingerprint_AcrossRecipes()
    {
        var recipeA = MakeRecipe("Sunday Pancakes", FlourId, 2m, CupId);
        var recipeB = MakeRecipe("Banana Bread", FlourId, 5m, CupId); // different quantity, same unit pair
        var recipes = new FakeD2RecipeRepository([recipeA, recipeB]);
        var products = new FakeD2CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true, GramId);

        var findings = await BuildDetector(recipes, products, new FakeD2UnitConverter(succeeds: false)).DetectAsync();

        Assert.Equal(2, findings.Count);
        Assert.Equal(findings[0].FactsFingerprint, findings[1].FactsFingerprint);
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId, 2m, CupId);
        var recipes = new FakeD2RecipeRepository([recipe]);
        var products = new FakeD2CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true, GramId);

        var detector = BuildDetector(recipes, products, new FakeD2UnitConverter(succeeds: false), new FakeD2TenantContext(null));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class FakeD2TenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakeD2RecipeRepository(IReadOnlyList<Recipe> recipes) : IRecipeRepository
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

file sealed class FakeD2CatalogProductReader : ICatalogProductReader
{
    private readonly Dictionary<Guid, CatalogProduct> _products = [];

    public void Add(Guid id, string name, bool trackStock, Guid defaultUnitId) =>
        _products[id] = new CatalogProduct(id, name, trackStock, defaultUnitId, ParentProductId: null, IsParent: false, VariantProductIds: []);

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

file sealed class FakeD2UnitConverter(bool succeeds) : IUnitConverter
{
    public Task<Result<decimal>> ConvertAsync(Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
        Task.FromResult(succeeds
            ? Result<decimal>.Success(amount)
            : Result<decimal>.Failure(Error.Custom("Test.NoConversion", "No conversion factor.")));
}
