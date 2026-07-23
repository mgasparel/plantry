using Plantry.Housekeeping.Domain;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Housekeeping;
using Xunit;

namespace Plantry.Tests.Web.Housekeeping;

/// <summary>
/// L2 golden tests for <see cref="RecipeIngredientNoPriceDetector"/> (D5, tidy-up.md §3) — including the
/// TrackStock exclusion boundary and fingerprint pinning: D5's gap is binary, so the fingerprint is
/// constant per subject regardless of the underlying facts (§4).
///
/// Tests live in Plantry.Tests.Web because the detector is in Plantry.Composition (referenced
/// transitively via Plantry.Web) — mirrors <c>ShoppingPantryReaderAdapterTests</c>' rationale.
/// </summary>
public sealed class RecipeIngredientNoPriceDetectorTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    private static readonly Guid FlourId = Guid.Parse("11111111-1111-1111-1111-0000000000d5");
    private static readonly Guid VanillaId = Guid.Parse("11111111-1111-1111-1111-0000000000d6");
    private static readonly Guid GramId = Guid.Parse("22222222-2222-2222-2222-0000000000d6");

    private static Recipe MakeRecipe(string name, Guid productId)
    {
        var recipe = Recipe.Create(Household, name, defaultServings: 4, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(productId, 200m, GramId, GroupHeading: null, Ordinal: 0)], Clock);
        return recipe;
    }

    private static RecipeIngredientNoPriceDetector BuildDetector(
        IRecipeRepository recipes, ICatalogProductReader products, IPriceObservationRepository priceRepo,
        ITenantContext? tenant = null) =>
        new(recipes, products, new PricingQueries(priceRepo), tenant ?? new FakeD5TenantContext(Household.Value));

    [Fact(DisplayName = "Tracked product with zero price observations — produces a finding")]
    public async Task TrackedProductNoPrice_ProducesFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId);
        var recipes = new FakeD5RecipeRepository([recipe]);
        var products = new FakeD5CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true);

        var detector = BuildDetector(recipes, products, new FakeD5PriceRepository());
        var findings = await detector.DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.RecipeIngredientNoPriceData, finding.DetectorId);
        Assert.Equal(FlourId, finding.SubjectId);
        Assert.Equal("All-Purpose Flour", finding.SubjectName);
        Assert.Contains("Sunday Pancakes", finding.Specifics);
        Assert.Equal("/Pantry/Products/Detail/" + FlourId, finding.FixUrl);
        Assert.Equal("Set price in Pantry", finding.FixLabel);
    }

    [Fact(DisplayName = "Tracked product WITH a price observation — no finding")]
    public async Task TrackedProductWithPrice_NoFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId);
        var recipes = new FakeD5RecipeRepository([recipe]);
        var products = new FakeD5CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true);

        var priceRepo = new FakeD5PriceRepository();
        priceRepo.AddObservation(FlourId);

        var detector = BuildDetector(recipes, products, priceRepo);
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Untracked product — excluded even with zero price observations (D7's territory)")]
    public async Task UntrackedProduct_NoFinding()
    {
        var recipe = MakeRecipe("Sunday Pancakes", VanillaId);
        var recipes = new FakeD5RecipeRepository([recipe]);
        var products = new FakeD5CatalogProductReader();
        products.Add(VanillaId, "Vanilla Extract", trackStock: false);

        var detector = BuildDetector(recipes, products, new FakeD5PriceRepository());
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Product used in two recipes — Specifics reports the recipe count")]
    public async Task UsedInMultipleRecipes_ReportsCount()
    {
        var recipeA = MakeRecipe("Sunday Pancakes", FlourId);
        var recipeB = MakeRecipe("Banana Bread", FlourId);
        var recipes = new FakeD5RecipeRepository([recipeA, recipeB]);
        var products = new FakeD5CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true);

        var detector = BuildDetector(recipes, products, new FakeD5PriceRepository());
        var finding = Assert.Single(await detector.DetectAsync());

        Assert.Contains("2 recipes", finding.Specifics);
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var recipe = MakeRecipe("Sunday Pancakes", FlourId);
        var recipes = new FakeD5RecipeRepository([recipe]);
        var products = new FakeD5CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true);

        var detector = BuildDetector(recipes, products, new FakeD5PriceRepository(), new FakeD5TenantContext(null));
        var findings = await detector.DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Fingerprint pinning: constant across different products (binary gap)")]
    public async Task Fingerprint_ConstantAcrossDifferentProducts()
    {
        var recipeA = MakeRecipe("Sunday Pancakes", FlourId);
        var recipes = new FakeD5RecipeRepository([recipeA]);
        var products = new FakeD5CatalogProductReader();
        products.Add(FlourId, "All-Purpose Flour", trackStock: true);
        var findingA = Assert.Single(await BuildDetector(recipes, products, new FakeD5PriceRepository()).DetectAsync());

        var sugarId = Guid.Parse("11111111-1111-1111-1111-0000000000d7");
        var recipeB = MakeRecipe("Banana Bread", sugarId);
        var recipesB = new FakeD5RecipeRepository([recipeB]);
        var productsB = new FakeD5CatalogProductReader();
        productsB.Add(sugarId, "Sugar", trackStock: true);
        var findingB = Assert.Single(await BuildDetector(recipesB, productsB, new FakeD5PriceRepository()).DetectAsync());

        Assert.Equal(findingA.FactsFingerprint, findingB.FactsFingerprint);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

file sealed class FakeD5TenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

file sealed class FakeD5RecipeRepository(IReadOnlyList<Recipe> recipes) : IRecipeRepository
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

file sealed class FakeD5CatalogProductReader : ICatalogProductReader
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

/// <summary>Only <see cref="ProductIdsWithAnyObservationAsync"/> is exercised by this detector; every other
/// member throws so an accidental dependency on a different read path fails loudly.</summary>
file sealed class FakeD5PriceRepository : IPriceObservationRepository
{
    private readonly HashSet<Guid> _live = [];

    public void AddObservation(Guid productId) => _live.Add(productId);

    public Task<IReadOnlySet<Guid>> ProductIdsWithAnyObservationAsync(IEnumerable<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(productIds.Where(_live.Contains).ToHashSet());

    public Task AddAsync(PriceObservation observation, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SaveChangesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PriceObservation?> FindAsync(PriceObservationId id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default) => throw new NotSupportedException();
}
