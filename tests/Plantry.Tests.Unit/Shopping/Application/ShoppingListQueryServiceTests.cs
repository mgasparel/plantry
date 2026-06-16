using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;

namespace Plantry.Tests.Unit.Shopping.Application;

/// <summary>
/// L2 unit tests for <see cref="ShoppingListQueryService"/>.
/// Verifies that Note and CategoryHue round-trip through the view model
/// (plantry-77f acceptance criteria). CategoryCode derivation is intentionally
/// not in the read model — downstream markup feeds <c>CatChipViewModel(CategoryName, CategoryHue)</c>
/// to the shared <c>_CatChip</c> partial, which owns the code derivation.
/// </summary>
public sealed class ShoppingListQueryServiceTests
{
    private static readonly IClock Clock = SystemClock.Instance;

    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ShoppingListQueryService BuildService(
        FakeShoppingListRepository repo,
        FakeShoppingCatalogReaderWithSummaries catalog)
    {
        return new ShoppingListQueryService(repo, catalog, new FakeTenantContext(_household));
    }

    private ShoppingList SeedListWithProductItem(
        FakeShoppingListRepository repo,
        string? note)
    {
        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 1m, unitId: _unitId, note: note,
            source: ItemSource.Manual, sourceRef: null, Clock);
        repo.Seed(list);
        return list;
    }

    private ShoppingList SeedListWithFreeTextItem(
        FakeShoppingListRepository repo,
        string? note)
    {
        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddFreeTextItem("Sourdough bread", quantity: null, unitId: null, note: note, Clock);
        repo.Seed(list);
        return list;
    }

    // ── Note round-trip ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GetList — product item note persisted on domain entity surfaces on view model")]
    public async Task GetList_ProductItem_NoteRoundTrips()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy", CategoryHue: 60));

        SeedListWithProductItem(repo, note: "skimmed only");

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items)
            .Concat(view.UncategorizedItems)
            .Concat(view.CheckedItems));
        Assert.Equal("skimmed only", item.Note);
    }

    [Fact(DisplayName = "GetList — free-text item note surfaces on view model")]
    public async Task GetList_FreeTextItem_NoteRoundTrips()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        SeedListWithFreeTextItem(repo, note: "gluten-free if possible");

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.UncategorizedItems);
        Assert.Equal("gluten-free if possible", item.Note);
    }

    [Fact(DisplayName = "GetList — null note on domain entity produces null Note on view model")]
    public async Task GetList_NullNote_ProducesNullNoteOnView()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy", CategoryHue: 60));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Null(item.Note);
    }

    // ── CategoryHue round-trip ────────────────────────────────────────────────

    [Fact(DisplayName = "GetList — CategoryHue from catalog summary threads through to view model")]
    public async Task GetList_CategoryHue_RoundTrips()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy", CategoryHue: 200));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(200, item.CategoryHue);
    }

    [Fact(DisplayName = "GetList — uncategorised product (null CategoryHue) produces null hue on view model")]
    public async Task GetList_UncategorisedProduct_ProducesNullHue()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Generic Product", null, CategoryHue: null));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        // Product with no category lands in UncategorizedItems
        var item = Assert.Single(view.UncategorizedItems);
        Assert.Null(item.CategoryHue);
    }

    [Fact(DisplayName = "GetList — free-text item (no catalog entry) produces null CategoryHue on view model")]
    public async Task GetList_FreeTextItem_ProducesNullCategoryHue()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        SeedListWithFreeTextItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.UncategorizedItems);
        Assert.Null(item.CategoryHue);
    }

    // ── Combined hue + note round-trip ────────────────────────────────────────

    [Fact(DisplayName = "GetList — CategoryHue and Note both round-trip together through the view")]
    public async Task GetList_HueAndNote_BothRoundTripTogether()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Cheddar", "Dairy & Eggs", CategoryHue: 55));

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 250m, unitId: _unitId, note: "mature preferred",
            source: ItemSource.Manual, sourceRef: null, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(55, item.CategoryHue);
        Assert.Equal("mature preferred", item.Note);
    }
}

/// <summary>
/// Variant of <see cref="FakeShoppingCatalogReader"/> that supports registering
/// specific product summaries (including CategoryHue) for query service tests.
/// </summary>
internal sealed class FakeShoppingCatalogReaderWithSummaries : IShoppingCatalogReader
{
    private readonly Dictionary<Guid, ShoppingProductSummary> _summaries = [];
    private readonly Dictionary<(Guid from, Guid to, Guid product), decimal> _conversions = [];

    public void RegisterSummary(Guid productId, ShoppingProductSummary summary) =>
        _summaries[productId] = summary;

    public void RegisterConversion(Guid fromUnitId, Guid toUnitId, Guid productId, decimal convertedAmount) =>
        _conversions[(fromUnitId, toUnitId, productId)] = convertedAmount;

    public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, ShoppingProductSummary> result = productIds
            .Where(_summaries.ContainsKey)
            .ToDictionary(id => id, id => _summaries[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ShoppingProductCandidate>>([]);

    public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default)
    {
        decimal? result = _conversions.TryGetValue((fromUnitId, toUnitId, productId), out var converted)
            ? converted
            : null;
        return Task.FromResult(result);
    }
}
