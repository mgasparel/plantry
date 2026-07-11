using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using InventoryDomain = Plantry.Inventory.Domain;

namespace Plantry.Tests.Unit.SharedKernel;

/// <summary>
/// Unit tests for <see cref="DomainTelemetry"/> — counter names and increment behaviour.
/// <para>
/// Uses <see cref="MeterListener"/> to subscribe to the <c>Plantry.Domain</c> meter and
/// record counter deltas, then exercises each instrumented command to verify the expected
/// counter increments. Mirrors the pattern from <c>AiTelemetryTests</c>.
/// </para>
/// <para>
/// Serialised (non-parallel) within the collection: the global <c>MeterListener</c> captures
/// all meter events process-wide, so concurrently running tests that also exercise the same
/// counters would produce spurious increments and flaky assertions.
/// </para>
/// </summary>
[Collection("DomainMeterListenerTests")]
public sealed class DomainTelemetryTests
{
    // ── Meter / counter name contracts ──────────────────────────────────────────────────────────

    [Fact]
    public void MeterName_Is_PlantryDomain()
    {
        // ServiceDefaults registers AddMeter("Plantry.Domain").
        // Changing this constant without updating ServiceDefaults would silently stop
        // all domain metrics from being exported.
        Assert.Equal("Plantry.Domain", DomainTelemetry.MeterName);
    }

    [Fact]
    public void IntakeSessionsCommitted_Counter_Name_Is_Correct()
    {
        Assert.Equal("plantry.intake.sessions_committed", DomainTelemetry.IntakeSessionsCommitted.Name);
    }

    [Fact]
    public void StockConsumed_Counter_Name_Is_Correct()
    {
        Assert.Equal("plantry.inventory.stock_consumed", DomainTelemetry.StockConsumed.Name);
    }

    [Fact]
    public void LowStockEvents_Counter_Name_Is_Correct()
    {
        Assert.Equal("plantry.inventory.low_stock_events", DomainTelemetry.LowStockEvents.Name);
    }

    [Fact]
    public void RecipesCooked_Counter_Name_Is_Correct()
    {
        Assert.Equal("plantry.recipes.cooked", DomainTelemetry.RecipesCooked.Name);
    }

    // ── Counter increment behaviour ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to the <c>Plantry.Domain</c> meter for the duration of the test and accumulates
    /// counter deltas, so individual tests can read the increment that occurred during their act phase.
    /// </summary>
    private sealed class DomainMeterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _totals = new();

        public DomainMeterListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == DomainTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                _totals.TryGetValue(instrument.Name, out var current);
                _totals[instrument.Name] = current + measurement;
            });
            _listener.Start();
        }

        public long Read(string counterName)
        {
            _listener.RecordObservableInstruments();
            return _totals.GetValueOrDefault(counterName);
        }

        public void Dispose() => _listener.Dispose();
    }

    // ── CommitSessionCommand emits IntakeSessionsCommitted ──────────────────────────────────────

    [Fact]
    public async Task CommitSession_Increments_IntakeSessionsCommitted_On_Success()
    {
        using var meter = new DomainMeterListener();

        var clock = SystemClock.Instance;
        var household = Guid.NewGuid();
        var unitId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var session = ImportSession.Start(HouseholdId.From(household), ImportSourceType.Receipt, userId, clock);
        var line = session.AddLine(1, "Flour 1kg", SuggestedConfidence.High, null);
        session.MarkReady(null, clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, unitId, locationId, null, price: null);

        var repo = new MetricsTestIntakeSessionRepository();
        repo.Sessions.Add(session);

        var cmd = new CommitSessionCommand(
            session.Id, repo,
            new MetricsTestCreateProductPort(),
            new MetricsTestAddStockPort(),
            new MetricsTestRecordPricePort(),
            new MetricsTestEnsurePurchaseStorePort(),
            new MetricsTestReviewReferenceDataProvider(),
            new MetricsTestSeedConversionPort(),
            clock, new MetricsTestTenantContext(household),
            NullLogger<CommitSessionCommand>.Instance);

        var before = meter.Read("plantry.intake.sessions_committed");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.intake.sessions_committed");

        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task CommitSession_Does_Not_Increment_IntakeSessionsCommitted_On_Failure()
    {
        using var meter = new DomainMeterListener();

        var clock = SystemClock.Instance;
        var household = Guid.NewGuid();

        var session = ImportSession.Start(HouseholdId.From(household), ImportSourceType.Receipt, Guid.NewGuid(), clock);
        // Not marked Ready — ExecuteAsync returns SessionNotReady error; no counter increment.
        var repo = new MetricsTestIntakeSessionRepository();
        repo.Sessions.Add(session);

        var cmd = new CommitSessionCommand(
            session.Id, repo,
            new MetricsTestCreateProductPort(),
            new MetricsTestAddStockPort(),
            new MetricsTestRecordPricePort(),
            new MetricsTestEnsurePurchaseStorePort(),
            new MetricsTestReviewReferenceDataProvider(),
            new MetricsTestSeedConversionPort(),
            clock, new MetricsTestTenantContext(household),
            NullLogger<CommitSessionCommand>.Instance);

        var before = meter.Read("plantry.intake.sessions_committed");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.intake.sessions_committed");

        Assert.Equal(before, after);
    }

    // ── ConsumeStockCommand emits StockConsumed ──────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeStock_Increments_StockConsumed_On_Success()
    {
        using var meter = new DomainMeterListener();

        var (stocks, productId, unitId, locationId) = BuildInventoryWithLot(100m);

        var cmd = BuildConsumeCommand(stocks, productId, unitId, amount: 30m);

        var before = meter.Read("plantry.inventory.stock_consumed");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.inventory.stock_consumed");

        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task ConsumeStock_Does_Not_Increment_StockConsumed_When_No_Stock_Record()
    {
        using var meter = new DomainMeterListener();

        var household = Guid.NewGuid();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var stocks = new MetricsTestStockRepository(); // empty — no stock record

        var cmd = new ConsumeStockCommand(
            productId, 30m, unitId, InventoryDomain.StockReason.Consumed, Guid.CreateVersion7(), null, null,
            stocks, new MetricsTestCatalogReadFacade(), new MetricsTestConversionProvider(), SystemClock.Instance,
            new MetricsTestTenantContext(household));

        var before = meter.Read("plantry.inventory.stock_consumed");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.inventory.stock_consumed");

        Assert.Equal(before, after);
    }

    // ── ConsumeStockCommand emits LowStockEvents when threshold is crossed ───────────────────────

    [Fact]
    public async Task ConsumeStock_Increments_LowStockEvents_When_Threshold_Crossed()
    {
        using var meter = new DomainMeterListener();

        // Threshold = 20; stock = 30; consume 20 → leaves 10 ≤ 20 → event fires.
        var (stocks, productId, unitId, _) = BuildInventoryWithLot(30m, threshold: 20m);

        var cmd = BuildConsumeCommand(stocks, productId, unitId, amount: 20m);

        var before = meter.Read("plantry.inventory.low_stock_events");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.inventory.low_stock_events");

        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task ConsumeStock_Does_Not_Increment_LowStockEvents_When_No_Threshold_Set()
    {
        using var meter = new DomainMeterListener();

        var (stocks, productId, unitId, _) = BuildInventoryWithLot(100m); // no threshold

        var cmd = BuildConsumeCommand(stocks, productId, unitId, amount: 30m);

        var before = meter.Read("plantry.inventory.low_stock_events");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.inventory.low_stock_events");

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ConsumeStock_Does_Not_Increment_LowStockEvents_When_Still_Above_Threshold()
    {
        using var meter = new DomainMeterListener();

        // Threshold = 20; stock = 100; consume 10 → leaves 90 > 20 → no event.
        var (stocks, productId, unitId, _) = BuildInventoryWithLot(100m, threshold: 20m);

        var cmd = BuildConsumeCommand(stocks, productId, unitId, amount: 10m);

        var before = meter.Read("plantry.inventory.low_stock_events");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.inventory.low_stock_events");

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ConsumeStock_Increments_LowStockEvents_Using_DisplayUnit_Conversion_For_Mixed_Unit_Stocks()
    {
        using var meter = new DomainMeterListener();

        // Mixed-unit scenario: lot stored in "g" (gramUnitId), threshold configured in "kg"
        // (kgUnitId, which is the product's DefaultUnitId/display unit).
        // Conversion factor: 1 g = 0.001 kg (i.e. multiply by 0.001 to go from g → kg).
        // Stock = 30 000 g = 30 kg; threshold = 25 kg; consume 10 000 g (10 kg) → 20 kg ≤ 25 kg → event fires.
        var clock = SystemClock.Instance;
        var household = Guid.NewGuid();
        var productId = Guid.CreateVersion7();
        var gramUnitId = Guid.CreateVersion7();  // lot unit
        var kgUnitId = Guid.CreateVersion7();    // display unit (DefaultUnitId)
        var locationId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var stocks = new MetricsTestStockRepository();
        var stock = InventoryDomain.ProductStock.Start(HouseholdId.From(household), productId, clock);
        stock.AddStock(30_000m, gramUnitId, locationId, userId, clock);
        stock.SetLowStockThreshold(25m, clock); // threshold in kg
        stocks.Items.Add(stock);
        stocks.HouseholdId = household;

        var factors = new Dictionary<(Guid From, Guid To), decimal>
        {
            [(gramUnitId, kgUnitId)] = 0.001m,  // g → kg
            [(kgUnitId, gramUnitId)] = 1000m,    // kg → g (for the consume itself)
        };
        // The consume is in gramUnitId → kgUnitId conversion not needed for the consume itself (same unit).
        // Consume amount 10 000 g in gramUnitId, so converter only needs identity for that direction.
        var converter = new MetricsTestFactorConverter(gramUnitId, kgUnitId, 0.001m);

        var catalog = new MetricsTestCatalogReadFacade(productId, kgUnitId); // display unit = kg
        var cmd = new ConsumeStockCommand(
            productId, 10_000m, gramUnitId, InventoryDomain.StockReason.Consumed,
            Guid.CreateVersion7(), null, null,
            stocks, catalog, new MetricsTestSingleFactorConversionProvider(converter),
            SystemClock.Instance, new MetricsTestTenantContext(household));

        var before = meter.Read("plantry.inventory.low_stock_events");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.inventory.low_stock_events");

        // 30 000 g − 10 000 g = 20 000 g = 20 kg ≤ 25 kg → event must fire.
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task ConsumeStock_Uses_RawSum_Fallback_For_Incompatible_Unit_Stocks_Matching_ReadPath()
    {
        using var meter = new DomainMeterListener();

        // Incompatible-unit scenario: lots in "ea" (eaUnitId), product's DefaultUnitId is "g" (gUnitId).
        // No conversion factor between ea and g — SumInDisplayUnit returns 0.
        // The DisplayQuantity fallback on the read path uses the raw sum (5 ea) in this case.
        // With threshold = 3 ea, the raw sum 5 ea > 3 → no event.
        // Without the fallback, onHand=0 ≤ 3 and the counter would over-fire.
        var clock = SystemClock.Instance;
        var household = Guid.NewGuid();
        var productId = Guid.CreateVersion7();
        var eaUnitId = Guid.CreateVersion7();   // lot unit (incompatible with display unit)
        var gUnitId = Guid.CreateVersion7();    // display unit (DefaultUnitId) — no conversion to ea
        var locationId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var stocks = new MetricsTestStockRepository();
        var stock = InventoryDomain.ProductStock.Start(HouseholdId.From(household), productId, clock);
        stock.AddStock(5m, eaUnitId, locationId, userId, clock);
        stock.SetLowStockThreshold(3m, clock); // threshold in display unit (g/ea-equivalent)
        stocks.Items.Add(stock);
        stocks.HouseholdId = household;

        // Converter with no ea→g factor — SumInDisplayUnit returns 0 for ea lots.
        var unconvertibleConverter = new MetricsTestFactorConverter(Guid.Empty, Guid.Empty, 1m); // never matches

        var catalog = new MetricsTestCatalogReadFacade(productId, gUnitId); // display unit = g
        var cmd = new ConsumeStockCommand(
            productId, 0.001m, eaUnitId, InventoryDomain.StockReason.Consumed,
            Guid.CreateVersion7(), null, null,
            stocks, catalog, new MetricsTestSingleFactorConversionProvider(unconvertibleConverter),
            SystemClock.Instance, new MetricsTestTenantContext(household));

        var before = meter.Read("plantry.inventory.low_stock_events");
        await cmd.ExecuteAsync();
        var after = meter.Read("plantry.inventory.low_stock_events");

        // 5 ea − 0.001 ea ≈ 5 ea raw sum > 3 threshold → no event (matches read path; no over-fire).
        Assert.Equal(before, after);
    }

    // ── CookRecipe emits RecipesCooked ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CookRecipe_Increments_RecipesCooked_On_Success()
    {
        using var meter = new DomainMeterListener();

        var clock = SystemClock.Instance;
        var household = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var unitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        var recipes = new MetricsTestRecipeRepository();
        var cookEvents = new MetricsTestCookEventRepository();
        var consumer = new MetricsTestInventoryConsumer();
        var producer = new MetricsTestInventoryProducer();
        var products = new MetricsTestCatalogProductReader();
        var dispatcher = new MetricsTestDomainEventDispatcher();
        var tenant = new MetricsTestTenantContext(household);
        var reconciler = new ReconcilePendingCooks(cookEvents, consumer, producer, tenant, NullLogger<ReconcilePendingCooks>.Instance);
        var deferredUnitGaps = new ApplyDeferredUnitGaps(cookEvents, consumer, tenant, NullLogger<ApplyDeferredUnitGaps>.Instance);

        products.AddTracked(productId, unitId);

        var recipe = Recipe.Create(HouseholdId.From(household), "Metrics Test Recipe", 4, clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, 100m, unitId, null, 0)], clock);
        recipes.Items.Add(recipe);

        var expansion = new RecipeExpansionService(recipes);
        var service = new CookRecipe(recipes, cookEvents, consumer, producer, products, expansion, dispatcher, clock, tenant, reconciler,
            deferredUnitGaps, NullLogger<CookRecipe>.Instance);

        var before = meter.Read("plantry.recipes.cooked");
        await service.ExecuteAsync(new CookRecipeCommand(recipe.Id, 4, userId, []));
        var after = meter.Read("plantry.recipes.cooked");

        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task CookRecipe_Does_Not_Increment_RecipesCooked_When_Recipe_Not_Found()
    {
        using var meter = new DomainMeterListener();

        var clock = SystemClock.Instance;
        var household = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var recipes = new MetricsTestRecipeRepository(); // empty
        var cookEvents = new MetricsTestCookEventRepository();
        var consumer = new MetricsTestInventoryConsumer();
        var producer = new MetricsTestInventoryProducer();
        var products = new MetricsTestCatalogProductReader();
        var dispatcher = new MetricsTestDomainEventDispatcher();
        var tenant = new MetricsTestTenantContext(household);
        var reconciler = new ReconcilePendingCooks(cookEvents, consumer, producer, tenant, NullLogger<ReconcilePendingCooks>.Instance);
        var deferredUnitGaps = new ApplyDeferredUnitGaps(cookEvents, consumer, tenant, NullLogger<ApplyDeferredUnitGaps>.Instance);

        var expansion = new RecipeExpansionService(recipes);
        var service = new CookRecipe(recipes, cookEvents, consumer, producer, products, expansion, dispatcher, clock, tenant, reconciler,
            deferredUnitGaps, NullLogger<CookRecipe>.Instance);

        var before = meter.Read("plantry.recipes.cooked");
        await service.ExecuteAsync(new CookRecipeCommand(RecipeId.New(), 4, userId, []));
        var after = meter.Read("plantry.recipes.cooked");

        Assert.Equal(before, after);
    }

    // ── Inventory helpers ────────────────────────────────────────────────────────────────────────

    private static (MetricsTestStockRepository Stocks, Guid ProductId, Guid UnitId, Guid LocationId)
        BuildInventoryWithLot(decimal quantity, decimal? threshold = null)
    {
        var clock = SystemClock.Instance;
        var household = Guid.NewGuid();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var stocks = new MetricsTestStockRepository();
        var stock = InventoryDomain.ProductStock.Start(HouseholdId.From(household), productId, clock);
        stock.AddStock(quantity, unitId, locationId, userId, clock);
        if (threshold.HasValue)
            stock.SetLowStockThreshold(threshold, clock);
        stocks.Items.Add(stock);
        stocks.HouseholdId = household;
        return (stocks, productId, unitId, locationId);
    }

    private static ConsumeStockCommand BuildConsumeCommand(
        MetricsTestStockRepository stocks, Guid productId, Guid unitId, decimal amount) =>
        new(productId, amount, unitId, InventoryDomain.StockReason.Consumed, Guid.CreateVersion7(), null, null,
            stocks, new MetricsTestCatalogReadFacade(productId, unitId), new MetricsTestConversionProvider(),
            SystemClock.Instance, new MetricsTestTenantContext(stocks.HouseholdId));
}

// ── Shared tenant context ────────────────────────────────────────────────────────────────────────

internal sealed class MetricsTestTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId => householdId;
}

// ── Intake test doubles ──────────────────────────────────────────────────────────────────────────

internal sealed class MetricsTestIntakeSessionRepository : IImportSessionRepository
{
    public List<ImportSession> Sessions { get; } = [];

    public Task<ImportSession?> FindAsync(ImportSessionId id, CancellationToken ct = default) =>
        Task.FromResult(Sessions.SingleOrDefault(s => s.Id == id));

    public Task AddAsync(ImportSession session, CancellationToken ct = default) { Sessions.Add(session); return Task.CompletedTask; }
    public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportReceipt?>(null);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default) => Task.FromResult(new List<ImportSession>());
    public Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) => Task.FromResult(new List<ImportSession>());
}

internal sealed class MetricsTestCreateProductPort : ICreateProductPort
{
    public Task<Guid> CreateAsync(string name, Guid categoryId, Guid defaultUnitId, CancellationToken ct = default) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class MetricsTestAddStockPort : IAddStockPort
{
    public Task<Guid> AddStockAsync(Guid productId, Guid? skuId, decimal quantity, Guid unitId, Guid locationId,
        DateOnly? expiryDate, DateOnly? purchasedAt, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class MetricsTestRecordPricePort : IRecordPricePort
{
    public Task<Guid> RecordAsync(Guid productId, Guid? skuId, decimal price, decimal quantity, Guid unitId,
        string? merchantText, Guid? storeId, Guid sourceRef, DateTimeOffset observedAt, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class MetricsTestEnsurePurchaseStorePort : IEnsurePurchaseStorePort
{
    public Task<Guid> EnsureAsync(string merchantName, CancellationToken ct = default) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class MetricsTestReviewReferenceDataProvider : IReviewReferenceDataProvider
{
    public Task<ReviewReferenceData> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(new ReviewReferenceData([], [], [], []));
}

internal sealed class MetricsTestSeedConversionPort : ISeedConversionPort
{
    public Task SeedAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default) =>
        Task.CompletedTask;
}

// ── Inventory test doubles ───────────────────────────────────────────────────────────────────────

internal sealed class MetricsTestStockRepository : InventoryDomain.IProductStockRepository
{
    public List<InventoryDomain.ProductStock> Items { get; } = [];
    public Guid HouseholdId { get; set; }

    public Task<InventoryDomain.ProductStock?> FindForUpdateAsync(HouseholdId h, Guid p, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == h && s.ProductId == p));

    public Task<InventoryDomain.ProductStock?> FindAsync(HouseholdId h, Guid p, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == h && s.ProductId == p));

    public Task<InventoryDomain.ProductStock?> FindWithHistoryAsync(HouseholdId h, Guid p, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == h && s.ProductId == p));

    public Task<List<InventoryDomain.ProductStock>> ListForHouseholdAsync(HouseholdId h, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(s => s.HouseholdId == h).ToList());

    public Task<bool> AnyForHouseholdAsync(HouseholdId h, CancellationToken ct = default) =>
        Task.FromResult(Items.Any(s => s.HouseholdId == h));

    public Task AddAsync(InventoryDomain.ProductStock stock, CancellationToken ct = default) { Items.Add(stock); return Task.CompletedTask; }

    public Task<bool> TryAddAndSaveAsync(InventoryDomain.ProductStock stock, CancellationToken ct = default)
    {
        var existing = Items.SingleOrDefault(s => s.HouseholdId == stock.HouseholdId && s.ProductId == stock.ProductId);
        if (existing is not null) return Task.FromResult(false);
        Items.Add(stock);
        return Task.FromResult(true);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        await work(ct);
}

internal sealed class MetricsTestCatalogReadFacade : ICatalogReadFacade
{
    private readonly CatalogProductInfo? _product;

    /// <summary>Returns an empty catalog (product lookup returns null — triggers the fallback raw-sum path).</summary>
    public MetricsTestCatalogReadFacade() { }

    /// <summary>Returns a single product entry so the display-unit conversion path is exercised.</summary>
    public MetricsTestCatalogReadFacade(Guid productId, Guid defaultUnitId)
    {
        _product = new CatalogProductInfo(productId, "Test Product", null, defaultUnitId, "u", CanHoldStock: true);
    }

    public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_product?.Id == productId ? _product : null);

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>(_product is not null ? [_product] : []);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

internal sealed class MetricsTestConversionProvider : IProductConversionProvider
{
    public Task<InventoryDomain.IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<InventoryDomain.IQuantityConverter>(new MetricsTestIdentityConverter());
}

internal sealed class MetricsTestIdentityConverter : InventoryDomain.IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
}

// ── Recipes test doubles ─────────────────────────────────────────────────────────────────────────

internal sealed class MetricsTestRecipeRepository : IRecipeRepository
{
    public List<Recipe> Items { get; } = [];

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(r => r.Id == id));

    public Task AddAsync(Recipe recipe, CancellationToken ct = default) { Items.Add(recipe); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>([]);
    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Items.Any(r => r.HouseholdId == householdId));
    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
        IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<RecipeId, string>>(new Dictionary<RecipeId, string>());

    public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);

    public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
        RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
}

internal sealed class MetricsTestCookEventRepository : ICookEventRepository
{
    public List<CookEvent> Items { get; } = [];

    public Task AddAsync(CookEvent cookEvent, CancellationToken ct = default) { Items.Add(cookEvent); return Task.CompletedTask; }
    public Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookEvent>>([]);
    public Task<IReadOnlyList<CookEvent>> ListWithPendingLinesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookEvent>>([]);
    public Task<IReadOnlyList<CookEvent>> ListWithDeferredUnitGapLinesForProductsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookEvent>>([]);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class MetricsTestInventoryConsumer : IInventoryConsumer
{
    public Task<ConsumeResult> ConsumeAsync(
        Guid productId, decimal quantity, Guid unitId,
        ConsumeReason reason, Guid cookEventId, Guid userId,
        Guid sourceLineRef, CancellationToken ct = default) =>
        Task.FromResult(new ConsumeResult(0m, unitId));
}

internal sealed class MetricsTestInventoryProducer : IInventoryProducer
{
    public Task ProduceAsync(
        Guid productId, decimal quantity, Guid unitId, DateOnly? expiryDate,
        ProduceReason reason, Guid cookEventId, Guid userId,
        Guid sourceLineRef, CancellationToken ct = default) =>
        Task.CompletedTask;
}

internal sealed class MetricsTestCatalogProductReader : ICatalogProductReader
{
    private readonly Dictionary<Guid, CatalogProduct> _products = new();

    public void AddTracked(Guid productId, Guid unitId) =>
        _products[productId] = new CatalogProduct(productId, "Test Product", TrackStock: true, unitId, null, false, []);

    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.GetValueOrDefault(productId));

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);

    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, CatalogProductSummary> result = productIds
            .Where(_products.ContainsKey)
            .ToDictionary(id => id, id => new CatalogProductSummary(id, _products[id].Name, _products[id].TrackStock));
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

internal sealed class MetricsTestDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>Converter that applies a single configured factor for one unit pair; identity otherwise.</summary>
internal sealed class MetricsTestFactorConverter(Guid fromId, Guid toId, decimal factor) : InventoryDomain.IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId)
    {
        if (fromUnitId == toUnitId) return amount;
        if (fromUnitId == fromId && toUnitId == toId) return amount * factor;
        return Error.Custom("Test.Unresolvable", $"no conversion from {fromUnitId} to {toUnitId}");
    }
}

/// <summary>Hands the same pre-built converter to every product — for single-product tests.</summary>
internal sealed class MetricsTestSingleFactorConversionProvider(InventoryDomain.IQuantityConverter converter) : IProductConversionProvider
{
    public Task<InventoryDomain.IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(converter);
}
