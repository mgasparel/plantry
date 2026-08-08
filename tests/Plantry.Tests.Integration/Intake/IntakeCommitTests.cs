using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Intake.Infrastructure;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Market.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Intake;
using Plantry.Pantry.Application;
using Plantry.Web.Pricing;
using Xunit;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Integration.Intake;

/// <summary>
/// L3 commit-path test: <see cref="CommitSessionCommand"/> wired over the REAL cross-context adapters
/// (the composition-root seams from Plantry.Web) against a real Postgres schema, with a fake AI upstream.
/// Proves a committed receipt lands a real Inventory lot/journal stamped <c>source = Intake</c> and a
/// real Pricing observation (<c>source = Purchase</c>), and that the EF household filter isolates other
/// households.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IntakeCommitTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private UnitId _gramsId;
    private ProductId _productId;
    private CategoryId _categoryId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed a base unit and a stock-holding product the receipt line resolves against.
        await using var catalog = NewCatalogDb();
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        await catalog.Units.AddAsync(grams);
        var product = Product.Create(_household, "Flour", grams.Id, Clock);
        await catalog.Products.AddAsync(product);
        var category = Category.Create(_household, "Pantry");
        await catalog.Categories.AddAsync(category);
        await catalog.SaveChangesAsync();
        _gramsId = grams.Id;
        _productId = product.Id;
        _categoryId = category.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Commit writes a real Intake-sourced stock journal + price observation")]
    public async Task Commit_Writes_Stock_And_Price()
    {
        // A Ready session with one confirmed line against the seeded product.
        ImportSessionId sessionId;
        await using (var setup = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            var line = session.AddLine(1, "FLOUR 1KG", SuggestedConfidence.High, """{"receipt_text":"FLOUR 1KG"}""");
            session.MarkReady("Superstore", Clock.UtcNow);
            line.Confirm(_productId.Value, skuId: null, 1000m, _gramsId.Value, _locationId, expiryDate: null, price: 4.99m);
            await setup.ImportSessions.AddAsync(session);
            await setup.SaveChangesAsync();
            sessionId = session.Id;
        }

        var tenant = new TestTenant(_household.Value);

        await using var catalogDb = NewCatalogDb();
        await using var inventoryDb = NewInventoryDb();
        await using var pricingDb = NewPricingDb();
        await using var intakeDb = NewIntakeDb();

        var products = new ProductRepository(catalogDb);
        var units = new UnitRepository(catalogDb);
        var categories = new CategoryRepository(catalogDb);
        var locations = new LocationRepository(catalogDb);
        var catalogFacade = new CatalogReadFacade(products, new UnitCodesAccessor(units), categories, locations, new FakeHouseholdExpiryDefaultsReader());

        var createProduct = new CreateProductAdapter(products, units, categories, locations, Clock, tenant);
        var addStock = new AddStockAdapter(new ProductStockRepository(inventoryDb), catalogFacade, Clock, tenant);
        var recordPrice = new RecordPriceAdapter(
            new PriceObservationRepository(pricingDb), new UnitPriceCalculatorAdapter(units), tenant,
            NullLogger<RecordObservationCommand>.Instance);
        var ensureStore = new EnsurePurchaseStoreAdapter(new StoreRepository(catalogDb), tenant, Clock);
        var referenceData = new ReviewReferenceDataProvider(products, units, locations, categories, new StoreRepository(catalogDb));
        var seedConversion = new SeedConversionAdapter(products, Clock, NullLogger<SeedConversionAdapter>.Instance);

        var command = new CommitSessionCommand(
            sessionId, new ImportSessionRepository(intakeDb), createProduct, addStock, recordPrice, ensureStore,
            referenceData, seedConversion, Clock, tenant, NullLogger<CommitSessionCommand>.Instance);

        var result = await command.ExecuteAsync();

        Assert.True(result.IsSuccess);

        // Inventory: one Intake-sourced purchase lot + journal row.
        await using var verifyInventory = NewInventoryDb();
        var stock = await verifyInventory.ProductStocks
            .Include(p => p.Journal)
            .SingleAsync(p => p.ProductId == _productId.Value);
        var journal = Assert.Single(stock.Journal);
        Assert.Equal(StockReason.Purchase, journal.Reason);
        Assert.Equal(StockSourceType.Intake, journal.SourceType);
        Assert.Equal(1000m, journal.Delta);

        // Pricing: one Purchase observation tied back to the session, with a normalized unit price.
        await using var verifyPricing = NewPricingDb();
        var observation = await verifyPricing.PriceObservations.SingleAsync();
        Assert.Equal(PriceSource.Purchase, observation.Source);
        Assert.Equal(_productId.Value, observation.ProductId);
        Assert.Equal(sessionId.Value, observation.SourceRef);
        Assert.Equal(4.99m, observation.Price);
        Assert.NotNull(observation.UnitPrice); // grams is the base unit → price / (1000 × 1)
        Assert.NotNull(observation.StoreId);   // receipt merchant resolved to a catalog.store (DM-16)

        // Catalog: the receipt merchant "Superstore" was resolved find-or-create to a manual store whose
        // id is exactly the one stamped on the purchase observation.
        await using var verifyCatalog = NewCatalogDb();
        var store = await verifyCatalog.Stores.SingleAsync();
        Assert.Equal("Superstore", store.Name);
        Assert.Null(store.ExternalRef);        // purchase-side manual store, not a Flipp subscription
        Assert.Equal(store.Id.Value, observation.StoreId);

        // Intake: session + line committed, with the cross-context refs recorded.
        await using var verifyIntake = NewIntakeDb();
        var committed = await verifyIntake.ImportSessions.Include(s => s.Lines).SingleAsync(s => s.Id == sessionId);
        Assert.Equal(ImportStatus.Committed, committed.Status);
        var committedLine = Assert.Single(committed.Lines);
        Assert.Equal(LineStatus.Committed, committedLine.Status);
        Assert.NotNull(committedLine.JournalId);
        Assert.NotNull(committedLine.PriceObservationId);
    }

    [Fact(DisplayName = "Household filter: another household cannot see the committed session")]
    public async Task Other_Household_Cannot_See_The_Session()
    {
        await using (var setup = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            await setup.ImportSessions.AddAsync(session);
            await setup.SaveChangesAsync();
        }

        await using var otherDb = NewIntakeDbFor(HouseholdId.New());
        Assert.Equal(0, await otherDb.ImportSessions.CountAsync());
    }

    [Fact(DisplayName = "A failed batch retry after reload reuses one staged Catalog product and preserves both lines")]
    public async Task Failed_batch_retry_after_reload_reuses_staged_product()
    {
        ImportSessionId sessionId;
        ImportLineId firstLineId;
        ImportLineId secondLineId;

        // Confirm two receipt lines against one explicit staged alias through the real application command,
        // then persist the Ready session before the commit attempt.
        await using (var setup = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            var first = session.AddLine(1, "OAT MILK 1L", SuggestedConfidence.None, rawPayload: null);
            var second = session.AddLine(2, "OAT MILK 2L", SuggestedConfidence.None, rawPayload: null);
            session.MarkReady("Resilient Grocer", Clock.UtcNow);
            await setup.ImportSessions.AddAsync(session);
            await setup.SaveChangesAsync();

            var tenant = new TestTenant(_household.Value);
            var sessions = new ImportSessionRepository(setup);
            var firstConfirm = await new ConfirmLineAsNewCommand(
                session.Id, first.Id, "Shared Oat Milk", _categoryId.Value, 1m, _gramsId.Value, _locationId,
                expiryDate: null, price: 4.99m, sessions, tenant).ExecuteAsync();
            Assert.True(firstConfirm.IsSuccess);

            var stagedId = (await setup.StagedProducts.SingleAsync()).Id;
            var secondConfirm = await new ConfirmLineAsNewCommand(
                session.Id, second.Id, "Shared Oat Milk", _categoryId.Value, 2m, _gramsId.Value, _locationId,
                expiryDate: null, price: 9.98m, sessions, tenant, stagedProductId: stagedId).ExecuteAsync();
            Assert.True(secondConfirm.IsSuccess);

            sessionId = session.Id;
            firstLineId = first.Id;
            secondLineId = second.Id;
        }

        // The first execution fails on line 2 after line 1 has fully committed. The staged alias and its
        // Catalog id were saved before line 1's stock write, so both survive a context/process boundary.
        var firstAttempt = await ExecuteCommitWithRealAdaptersAsync(
            sessionId, failAddStockOnCall: 2);
        Assert.True(firstAttempt.IsFailure);
        Assert.Equal("Intake.CommitFailed", firstAttempt.Error.Code);

        await using (var afterFailure = NewIntakeDb())
        {
            var persisted = await afterFailure.ImportSessions
                .Include(s => s.Lines)
                .Include(s => s.StagedProducts)
                .SingleAsync(s => s.Id == sessionId);
            Assert.Equal(ImportStatus.Ready, persisted.Status);
            Assert.Equal(LineStatus.Committed, persisted.Lines.Single(l => l.Id == firstLineId).Status);
            Assert.Equal(LineStatus.Confirmed, persisted.Lines.Single(l => l.Id == secondLineId).Status);
            Assert.Single(persisted.StagedProducts);
            Assert.NotNull(persisted.StagedProducts.Single().CreatedProductId);
        }

        var resumed = await ExecuteCommitWithRealAdaptersAsync(sessionId, failAddStockOnCall: null);
        Assert.True(resumed.IsSuccess);

        await using var verifyIntake = NewIntakeDb();
        var committed = await verifyIntake.ImportSessions
            .Include(s => s.Lines)
            .Include(s => s.StagedProducts)
            .SingleAsync(s => s.Id == sessionId);
        Assert.Equal(ImportStatus.Committed, committed.Status);
        Assert.All(committed.Lines.Where(l => l.Status == LineStatus.Committed), l => Assert.NotNull(l.JournalId));
        Assert.Equal(2, committed.Lines.Count(l => l.Status == LineStatus.Committed));
        var createdProductId = Assert.Single(committed.StagedProducts).CreatedProductId!.Value;
        Assert.Equal(createdProductId, committed.Lines.Single(l => l.Id == firstLineId).CreatedProductId);
        Assert.Equal(createdProductId, committed.Lines.Single(l => l.Id == secondLineId).CreatedProductId);

        await using var verifyCatalog = NewCatalogDb();
        Assert.Single(await verifyCatalog.Products.Where(p => p.Id == ProductId.From(createdProductId)).ToListAsync());

        await using var verifyInventory = NewInventoryDb();
        var stock = await verifyInventory.ProductStocks
            .Include(p => p.Journal)
            .SingleAsync(p => p.ProductId == createdProductId);
        Assert.Equal(2, stock.Journal.Count(j => j.SourceType == StockSourceType.Intake));
        Assert.Equal(2, stock.Journal.Where(j => j.SourceType == StockSourceType.Intake).Select(j => j.Id).Distinct().Count());

        await using var verifyPricing = NewPricingDb();
        var observations = await verifyPricing.PriceObservations
            .Where(p => p.ProductId == createdProductId)
            .ToListAsync();
        Assert.Equal(2, observations.Count);
        Assert.Equal(2, observations.Select(o => o.Id).Distinct().Count());
    }

    [Fact(DisplayName = "Discarding a staged new product never creates a Catalog row")]
    public async Task Discard_before_commit_does_not_materialize_staged_product()
    {
        ImportSessionId sessionId;
        await using (var setup = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            var line = session.AddLine(1, "ORPHAN OAT MILK", SuggestedConfidence.None, rawPayload: null);
            session.MarkReady("Discarded Grocer", Clock.UtcNow);
            await setup.ImportSessions.AddAsync(session);
            await setup.SaveChangesAsync();

            var tenant = new TestTenant(_household.Value);
            var sessions = new ImportSessionRepository(setup);
            var confirm = await new ConfirmLineAsNewCommand(
                session.Id, line.Id, "Discarded Oat Milk", _categoryId.Value, 1m, _gramsId.Value, _locationId,
                expiryDate: null, price: 2.99m, sessions, tenant).ExecuteAsync();
            Assert.True(confirm.IsSuccess);
            session.Discard();
            await setup.SaveChangesAsync();
            sessionId = session.Id;
        }

        await using var verifyIntake = NewIntakeDb();
        var discarded = await verifyIntake.ImportSessions
            .Include(s => s.StagedProducts)
            .SingleAsync(s => s.Id == sessionId);
        Assert.Equal(ImportStatus.Discarded, discarded.Status);
        Assert.Single(discarded.StagedProducts);
        Assert.Null(discarded.StagedProducts.Single().CreatedProductId);

        await using var verifyCatalog = NewCatalogDb();
        Assert.Empty(await verifyCatalog.Products.Where(p => p.Name == "Discarded Oat Milk").ToListAsync());
    }

    private async Task<Result> ExecuteCommitWithRealAdaptersAsync(ImportSessionId sessionId, int? failAddStockOnCall)
    {
        await using var catalogDb = NewCatalogDb();
        await using var inventoryDb = NewInventoryDb();
        await using var pricingDb = NewPricingDb();
        await using var intakeDb = NewIntakeDb();

        var tenant = new TestTenant(_household.Value);
        var products = new ProductRepository(catalogDb);
        var units = new UnitRepository(catalogDb);
        var categories = new CategoryRepository(catalogDb);
        var locations = new LocationRepository(catalogDb);
        var catalogFacade = new CatalogReadFacade(
            products, new UnitCodesAccessor(units), categories, locations,
            new FakeHouseholdExpiryDefaultsReader());
        var realAddStock = new AddStockAdapter(
            new ProductStockRepository(inventoryDb), catalogFacade, Clock, tenant);
        IAddStockPort addStock = failAddStockOnCall is { } fail
            ? new FailOnCallAddStockPort(realAddStock, fail)
            : realAddStock;

        var command = new CommitSessionCommand(
            sessionId,
            new ImportSessionRepository(intakeDb),
            new CreateProductAdapter(products, units, categories, locations, Clock, tenant),
            addStock,
            new RecordPriceAdapter(
                new PriceObservationRepository(pricingDb), new UnitPriceCalculatorAdapter(units), tenant,
                NullLogger<RecordObservationCommand>.Instance),
            new EnsurePurchaseStoreAdapter(new StoreRepository(catalogDb), tenant, Clock),
            new ReviewReferenceDataProvider(products, units, locations, categories, new StoreRepository(catalogDb)),
            new SeedConversionAdapter(products, Clock, NullLogger<SeedConversionAdapter>.Instance),
            Clock, tenant, NullLogger<CommitSessionCommand>.Instance);

        return await command.ExecuteAsync();
    }

    private PantryDbContext NewCatalogDb()
    {
        var ctx = new PantryDbContext(
            new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private PantryDbContext NewInventoryDb()
    {
        var ctx = new PantryDbContext(
            new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private MarketDbContext NewPricingDb()
    {
        var ctx = new MarketDbContext(
            new DbContextOptionsBuilder<MarketDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private IntakeDbContext NewIntakeDb()
    {
        var ctx = new IntakeDbContext(
            new DbContextOptionsBuilder<IntakeDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private IntakeDbContext NewIntakeDbFor(HouseholdId household)
    {
        var ctx = new IntakeDbContext(
            new DbContextOptionsBuilder<IntakeDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private sealed class TestTenant(Guid household) : ITenantContext
    {
        public Guid? HouseholdId { get; } = household;
    }

    private sealed class FailOnCallAddStockPort(IAddStockPort inner, int failOnCall) : IAddStockPort
    {
        private int _calls;

        public Task<Guid> AddStockAsync(
            Guid productId, Guid? skuId, decimal quantity, Guid unitId, Guid locationId,
            DateOnly? expiryDate, DateOnly? purchasedAt, Guid userId, Guid? sourceRef = null,
            CancellationToken ct = default)
        {
            if (++_calls == failOnCall)
                throw new InvalidOperationException("synthetic mid-batch failure");
            return inner.AddStockAsync(productId, skuId, quantity, unitId, locationId, expiryDate, purchasedAt,
                userId, sourceRef, ct);
        }
    }
}
