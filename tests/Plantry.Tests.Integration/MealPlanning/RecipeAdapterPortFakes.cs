using Plantry.Recipes.Application;
using Plantry.SharedKernel;

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

    public static FakeCatalog WithTrackedLeaf(Guid productId, Guid unitId)
    {
        var c = new FakeCatalog();
        c._products[productId] = new CatalogProduct(productId, "Cheese", TrackStock: true, unitId, null, false, []);
        return c;
    }

    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.GetValueOrDefault(productId));

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);
    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, CatalogProductSummary>>(new Dictionary<Guid, CatalogProductSummary>());
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
