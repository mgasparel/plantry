using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L2 tests for <see cref="AddCountedItemCommand"/> — the inline-add command (P4-7, J5, C8).
/// Verifies:
///  - Create-then-count writes a tracked product + opening-balance Correction.
///  - A zero count registers the product but adds no stock.
///  - Duplicate name surfaces the Catalog error inline (Catalog.DuplicateProductName).
///  - Unauthorized tenant context blocks execution.
/// </summary>
public sealed class AddCountedItemCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fake <see cref="ITakeStockCatalogWriter"/> that returns a pre-configured product id on create
    /// or throws <see cref="InvalidOperationException"/> to simulate a Catalog rejection.
    /// </summary>
    private sealed class FakeTakeStockCatalogWriter(
        Guid? returnProductId = null, string? throwMessage = null, bool addConversionThrows = false)
        : ITakeStockCatalogWriter
    {
        public int CreateCalls { get; private set; }
        public int SetLocationCalls { get; private set; }
        public string? LastName { get; private set; }
        public Guid? LastUnitId { get; private set; }
        public Guid? LastLocationId { get; private set; }

        // ── AddConversion capture (plantry-bxzh) ──────────────────────────────
        public int ConversionCalls { get; private set; }
        public Guid LastConversionProductId { get; private set; }
        public Guid LastConversionFromUnitId { get; private set; }
        public Guid LastConversionToUnitId { get; private set; }
        public decimal LastConversionFactor { get; private set; }

        public Task<Guid> CreateTrackedProductAsync(
            string name, Guid defaultUnitId, Guid? categoryId, Guid? defaultLocationId, CancellationToken ct = default)
        {
            CreateCalls++;
            LastName = name;
            LastUnitId = defaultUnitId;
            LastLocationId = defaultLocationId;

            if (throwMessage is not null)
                throw new InvalidOperationException(throwMessage);

            return Task.FromResult(returnProductId ?? Guid.NewGuid());
        }

        public Task<Guid> CreateTrackedVariantAsync(
            Guid parentGroupId, string variantName,
            Guid? unitOverride, Guid? categoryOverride, Guid? locationOverride,
            CancellationToken ct = default)
        {
            CreateCalls++;
            LastName = variantName;
            LastLocationId = locationOverride;

            if (throwMessage is not null)
                throw new InvalidOperationException(throwMessage);

            return Task.FromResult(returnProductId ?? Guid.NewGuid());
        }

        public Task<Guid> CreateTrackedGroupedProductAsync(
            string groupName, string variantName,
            Guid defaultUnitId, Guid? categoryId, Guid? defaultLocationId,
            CancellationToken ct = default)
        {
            CreateCalls++;
            LastName = variantName;
            LastUnitId = defaultUnitId;
            LastLocationId = defaultLocationId;

            if (throwMessage is not null)
                throw new InvalidOperationException(throwMessage);

            return Task.FromResult(returnProductId ?? Guid.NewGuid());
        }

        public Task SetDefaultLocationAsync(Guid productId, Guid locationId, CancellationToken ct = default)
        {
            SetLocationCalls++;
            return Task.CompletedTask;
        }

        public Task AddConversionAsync(
            Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default)
        {
            ConversionCalls++;
            LastConversionProductId = productId;
            LastConversionFromUnitId = fromUnitId;
            LastConversionToUnitId = toUnitId;
            LastConversionFactor = factor;

            if (addConversionThrows)
                throw new InvalidOperationException(
                    "Add product conversion failed (Catalog.InvalidConversion): factor must be positive.");

            return Task.CompletedTask;
        }

        public Task MarkLocationCountedAsync(Guid locationId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private AddCountedItemCommand Command(
        ITakeStockCatalogWriter writer,
        FakeProductStockRepository stocks,
        string name = "New Product",
        decimal countedValue = 5m,
        Guid? unitId = null,
        Guid? locationId = null,
        Guid? household = null)
    {
        var tenant = new FakeTenantContext(household ?? _household);
        var converter = new FakeConversionProvider(new IdentityQuantityConverter());

        return new AddCountedItemCommand(
            name,
            unitId ?? _unitId,
            locationId ?? _locationId,
            countedValue,
            unitId ?? _unitId,
            _userId,
            writer,
            stocks,
            converter,
            Clock,
            tenant);
    }

    // ── L2: create-then-count ─────────────────────────────────────────────────

    [Fact(DisplayName = "Create + count: creates product and records opening-balance Correction")]
    public async Task CreateAndCount_WritesTrackedProduct_AndOpeningBalanceCorrection()
    {
        var newProductId = Guid.CreateVersion7();
        var writer = new FakeTakeStockCatalogWriter(returnProductId: newProductId);
        var stocks = new FakeProductStockRepository();

        var cmd = Command(writer, stocks, name: "Oat Milk", countedValue: 5m);
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error.Description}");
        Assert.Equal(newProductId, result.Value);

        // Writer was called with the right name + location.
        Assert.Equal(1, writer.CreateCalls);
        Assert.Equal("Oat Milk", writer.LastName);
        Assert.Null(writer.LastLocationId);

        // Stock root was created with a Correction lot for 5m.
        var stock = stocks.Items.SingleOrDefault(s => s.ProductId == newProductId);
        Assert.NotNull(stock);
        var correctionJournals = stock.Journal
            .Where(j => j.Reason == StockReason.Correction && j.Delta > 0)
            .ToList();
        Assert.Single(correctionJournals);
        Assert.Equal(5m, correctionJournals[0].Delta);
    }

    [Fact(DisplayName = "Create with zero count: registers product but adds no stock lot")]
    public async Task CreateWithZeroCount_RegistersProduct_NoStockLot()
    {
        var newProductId = Guid.CreateVersion7();
        var writer = new FakeTakeStockCatalogWriter(returnProductId: newProductId);
        var stocks = new FakeProductStockRepository();

        var cmd = Command(writer, stocks, name: "Empty Shelf Product", countedValue: 0m);
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(newProductId, result.Value);
        Assert.Equal(1, writer.CreateCalls);

        // No stock root should be created for a zero count.
        var stock = stocks.Items.SingleOrDefault(s => s.ProductId == newProductId);
        Assert.Null(stock);
    }

    // ── L2: duplicate name surfaces Catalog error ─────────────────────────────

    [Fact(DisplayName = "Duplicate name: Catalog rejection surfaces as Inventory.InlineAddFailed")]
    public async Task DuplicateName_CatalogRejection_SurfacesAsInlineAddFailed()
    {
        var writer = new FakeTakeStockCatalogWriter(
            throwMessage: "Create tracked product failed (Catalog.DuplicateProductName): A product named 'Flour' already exists.");
        var stocks = new FakeProductStockRepository();

        var cmd = Command(writer, stocks, name: "Flour", countedValue: 10m);
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InlineAddFailed", result.Error.Code);
        Assert.Contains("Flour", result.Error.Description);

        // No stock should have been written.
        Assert.Empty(stocks.Items);
    }

    // ── L2: conversionFactor persistence (plantry-bxzh) ───────────────────────
    // The unit-convertibility gate itself lives in Walk.cshtml.cs (OnPostAddItemAsync, mirroring
    // the pre-existing Save-path NeedsConversion backstop's page-handler placement) — it runs
    // BEFORE this command is ever constructed, so an unconvertible counted unit with no supplied
    // factor never reaches AddCountedItemCommand at all. These tests cover what IS this command's
    // responsibility: persisting a supplied factor, in the right order, once the gate has cleared it.

    [Fact(DisplayName = "conversionFactor: persisted via the writer BEFORE the opening count is recorded")]
    public async Task ConversionFactor_PersistedBeforeOpeningCount()
    {
        var newProductId = Guid.CreateVersion7();
        var writer = new FakeTakeStockCatalogWriter(returnProductId: newProductId);
        var stocks = new FakeProductStockRepository();
        var cupUnitId = Guid.CreateVersion7();

        var tenant = new FakeTenantContext(_household);
        var converter = new FakeConversionProvider(new IdentityQuantityConverter());
        var cmd = new AddCountedItemCommand(
            "Bubly Strawberry", _unitId, _locationId,
            12m, cupUnitId,
            _userId, writer, stocks, converter, Clock, tenant,
            conversionFactor: 120m);

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error.Description}");
        Assert.Equal(newProductId, result.Value);

        // Product created.
        Assert.Equal(1, writer.CreateCalls);

        // Conversion persisted for the right (product, fromUnit, toUnit, factor).
        Assert.Equal(1, writer.ConversionCalls);
        Assert.Equal(newProductId, writer.LastConversionProductId);
        Assert.Equal(cupUnitId, writer.LastConversionFromUnitId);
        Assert.Equal(_unitId, writer.LastConversionToUnitId);
        Assert.Equal(120m, writer.LastConversionFactor);

        // Opening-balance lot recorded in the counted unit (cup).
        var stock = stocks.Items.SingleOrDefault(s => s.ProductId == newProductId);
        Assert.NotNull(stock);
        var lot = Assert.Single(stock.Entries);
        Assert.Equal(12m, lot.Quantity);
        Assert.Equal(cupUnitId, lot.UnitId);
    }

    [Fact(DisplayName = "conversionFactor: not persisted when the counted unit already equals the default unit")]
    public async Task ConversionFactor_NotPersisted_WhenCountedUnitEqualsDefaultUnit()
    {
        var newProductId = Guid.CreateVersion7();
        var writer = new FakeTakeStockCatalogWriter(returnProductId: newProductId);
        var stocks = new FakeProductStockRepository();

        // The command below builds with unitId == countedUnitId (same _unitId), so even if the
        // (redundant) caller supplied a factor it must not be forwarded to the writer.
        var tenant = new FakeTenantContext(_household);
        var converter = new FakeConversionProvider(new IdentityQuantityConverter());
        var cmdWithFactor = new AddCountedItemCommand(
            "Same Unit Item", _unitId, _locationId, 5m, _unitId,
            _userId, writer, stocks, converter, Clock, tenant,
            conversionFactor: 99m);

        var result = await cmdWithFactor.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, writer.ConversionCalls);
    }

    [Fact(DisplayName = "conversionFactor: a writer rejection surfaces as Inventory.InlineAddFailed and records no count")]
    public async Task ConversionFactor_WriterRejection_SurfacesAsInlineAddFailed()
    {
        var newProductId = Guid.CreateVersion7();
        var writer = new FakeTakeStockCatalogWriter(returnProductId: newProductId, addConversionThrows: true);
        var stocks = new FakeProductStockRepository();
        var cupUnitId = Guid.CreateVersion7();

        var tenant = new FakeTenantContext(_household);
        var converter = new FakeConversionProvider(new IdentityQuantityConverter());
        var cmd = new AddCountedItemCommand(
            "Bubly Strawberry", _unitId, _locationId, 12m, cupUnitId,
            _userId, writer, stocks, converter, Clock, tenant,
            conversionFactor: 120m);

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InlineAddFailed", result.Error.Code);

        // The product itself was created (Catalog write succeeded) but no stock was recorded —
        // the conversion write failed, so RecordCountCommand never ran.
        Assert.Empty(stocks.Items);
    }

    // ── L2: unauthorized context ──────────────────────────────────────────────

    [Fact(DisplayName = "Unauthorized: null household id returns Unauthorized error")]
    public async Task Unauthorized_NullHousehold_ReturnsUnauthorized()
    {
        var writer = new FakeTakeStockCatalogWriter(returnProductId: Guid.NewGuid());
        var stocks = new FakeProductStockRepository();
        var tenant = new FakeTenantContext(householdId: null);
        var converter = new FakeConversionProvider(new IdentityQuantityConverter());

        var cmd = new AddCountedItemCommand(
            "Some Product", _unitId, _locationId, 5m, _unitId, _userId,
            writer, stocks, converter, Clock, tenant);

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Equal(0, writer.CreateCalls);
    }
}
