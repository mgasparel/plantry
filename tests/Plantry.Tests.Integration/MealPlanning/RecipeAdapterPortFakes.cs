using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Integration.MealPlanning;

// ── Minimal Recipes-port fakes (Inventory / Catalog / Pricing / units) ───────
//
// Shared across this folder's RecipeReadModelAdapter integration test suites
// (RecipeReadModelAdapterExpandedTests, RecipeReadModelAdapterYieldPhotoTests) — extracted here
// (plantry-f4dt critic pass 1) after this exact stub family had accreted a 2nd private copy across
// those two files. (WeekBagEnricherTests.cs and MealPlanVariantConversionParityTests.cs carry a
// differently-shaped Null* fake family for a different purpose — not copies of this one; check this
// file first regardless before adding a new Recipes-port fake anywhere in this folder.)

internal sealed class FakeStock : IInventoryStockReader
{
    private readonly Dictionary<Guid, ProductStock> _stock = [];
    public FakeStock Add(Guid productId, decimal available, Guid unitId)
    {
        _stock[productId] = new ProductStock(productId, available, unitId, null);
        return this;
    }
    public Task<ProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_stock.GetValueOrDefault(productId));
    public Task<IReadOnlyDictionary<Guid, ProductStock>> FindStockBatchAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ProductStock>>(
            productIds.Where(_stock.ContainsKey).ToDictionary(id => id, id => _stock[id]));
}

internal sealed class FakeCatalog : ICatalogProductReader
{
    private readonly Dictionary<Guid, CatalogProduct> _products = [];

    /// <summary>Ids marked home-produced (<c>Product.IsProduced</c>) for <see cref="ResolveSummariesAsync"/>
    /// (plantry-4osq). Empty by default so every existing scenario is unaffected — see <see cref="MarkProduced"/>.</summary>
    private readonly HashSet<Guid> _produced = [];

    public static FakeCatalog WithTrackedLeaf(Guid productId, Guid unitId)
    {
        var c = new FakeCatalog();
        c._products[productId] = new CatalogProduct(productId, "Cheese", TrackStock: true, unitId, null, false, []);
        return c;
    }

    /// <summary>Adds an additional tracked leaf product, fluent-chainable — for a scenario needing more
    /// than the single product <see cref="WithTrackedLeaf"/> seeds (e.g. a substitution edge's target
    /// AND substitute product).</summary>
    public FakeCatalog AddTrackedLeaf(Guid productId, Guid unitId, string name = "Product")
    {
        _products[productId] = new CatalogProduct(productId, name, TrackStock: true, unitId, null, false, []);
        return this;
    }

    /// <summary>Marks an already-registered product id as home-produced (<c>Product.IsProduced</c> = true,
    /// plantry-4osq), fluent-chainable, so a subsequent <see cref="ResolveSummariesAsync"/> reports it.</summary>
    public FakeCatalog MarkProduced(Guid productId)
    {
        _produced.Add(productId);
        return this;
    }

    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.GetValueOrDefault(productId));

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);
    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, CatalogProductSummary> result = productIds
            .Where(_products.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => new CatalogProductSummary(
                id, _products[id].Name, _products[id].TrackStock, _produced.Contains(id)));
        return Task.FromResult(result);
    }
    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);
    public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogGroupOption>>([]);
    public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogCategoryOption>>([]);
}

internal sealed class FakePrices : IPriceReader
{
    private readonly Dictionary<Guid, PricePoint> _prices = [];
    public static FakePrices With(Guid productId, decimal unitPrice, Guid unitId)
    {
        var p = new FakePrices();
        p._prices[productId] = new PricePoint(productId, unitPrice, 1m, unitId, unitPrice);
        return p;
    }
    public Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_prices.GetValueOrDefault(productId));
}

internal sealed class IdentityConverter : IUnitConverter
{
    public Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
        Task.FromResult(fromUnitId == toUnitId
            ? Result<decimal>.Success(amount)
            : Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No path.")));
}

internal sealed class FixedHorizon(int days) : IExpiringSoonHorizonReader
{
    public Task<int> GetDaysAsync(CancellationToken ct = default) => Task.FromResult(days);
}

/// <summary>
/// Substitution-edge fake for the pure/async FulfillmentService paths (plantry-aqpa.2). Empty by
/// default (no edges — every existing test in this folder predates substitution and must keep behaving
/// identically); call <see cref="Add"/> to opt a specific test into substitution edges.
/// </summary>
internal sealed class FakeSubstitutions : ISubstitutionReader
{
    private readonly Dictionary<Guid, List<SubstitutionEdge>> _byTarget = [];

    public FakeSubstitutions Add(SubstitutionEdge edge)
    {
        if (!_byTarget.TryGetValue(edge.TargetProductId, out var list))
            _byTarget[edge.TargetProductId] = list = [];
        list.Add(edge);
        return this;
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>>> ListByTargetProductIdsAsync(
        IReadOnlyList<Guid> targetProductIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>>>(
            targetProductIds
                .Where(_byTarget.ContainsKey)
                .ToDictionary(id => id, id => (IReadOnlyList<SubstitutionEdge>)_byTarget[id]));

    public Task<IReadOnlyList<SubstitutionEdge>> ListTouchingProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SubstitutionEdge>>([]);
}

/// <summary>Shared fixed clock for this folder's RecipeReadModelAdapter suites (missing-seam:iclock-web,
/// plantry-4tb4). <paramref name="zone"/> defaults to UTC (matching IClock.Zone's own default) — pass an
/// explicit zone only when a test exercises local-vs-UTC calendar-day behaviour.</summary>
internal sealed class FixedClock(DateTimeOffset now, TimeZoneInfo? zone = null) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
    public TimeZoneInfo Zone { get; } = zone ?? TimeZoneInfo.Utc;
}

/// <summary>
/// <see cref="IRecipeRepository"/> fake for constructing a bare <see cref="RecipeExpansionService"/> in
/// tests that only ever call its batched in-memory overload (<c>ExpandAsync(RecipeId,
/// IReadOnlyDictionary&lt;RecipeId,Recipe&gt;, CancellationToken)</c>) — that overload's resolver never
/// touches the injected repository, so every member here throws if actually invoked (a real call would
/// signal a test wiring bug, not expected behaviour). Shared by <see cref="WeekBagEnricherTests"/> and
/// <see cref="MealPlanVariantConversionParityTests"/>, both of which construct a
/// <c>WeekBagEnricher</c> — which needs a <see cref="RecipeExpansionService"/> instance but, for these
/// flat-recipe scenarios, never exercises expansion at all (plantry-yqse).
/// </summary>
internal sealed class NullRecipeRepository : IRecipeRepository
{
    private static NotSupportedException NotSupported() =>
        new("NullRecipeRepository should never be called — only RecipeExpansionService's batched " +
            "in-memory overload is exercised in these tests, which never touches the repository.");

    public Task AddAsync(Recipe recipe, CancellationToken ct = default) => throw NotSupported();
    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) => throw NotSupported();
    public Task SaveChangesAsync(CancellationToken ct = default) => throw NotSupported();
    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) => throw NotSupported();
    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(IReadOnlyList<RecipeId> ids, CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) => throw NotSupported();
}
