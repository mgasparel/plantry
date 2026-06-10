using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Intake.Infrastructure;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.Pricing.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Events;
using Plantry.Web.Intake;
using Plantry.Web.Inventory;
using Plantry.Web.Pricing;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Intake;

/// <summary>
/// L3 commit-path test: <see cref="CommitSessionCommand"/> wired over the REAL cross-context adapters
/// (the composition-root seams from Plantry.Web) against a real Postgres schema, with a fake AI upstream.
/// Proves a committed receipt lands a real Inventory lot/journal stamped <c>source = Intake</c> and a
/// real Pricing observation (<c>source = Purchase</c>), fires the <see cref="ImportSessionCommittedEvent"/>
/// through the dispatch interceptor, and that the EF household filter isolates other households.
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
        await catalog.SaveChangesAsync();
        _gramsId = grams.Id;
        _productId = product.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Commit writes a real Intake-sourced stock journal + price observation and fires the committed event")]
    public async Task Commit_Writes_Stock_And_Price_And_Fires_Event()
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

        // Capturing handler behind the real dispatcher + dispatch interceptor.
        var handler = new CapturingHandler();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IDomainEventHandler<ImportSessionCommittedEvent>>(handler)
            .BuildServiceProvider();
        var interceptor = new DomainEventDispatchInterceptor(new DomainEventDispatcher(serviceProvider));

        var tenant = new TestTenant(_household.Value);

        await using var catalogDb = NewCatalogDb();
        await using var inventoryDb = NewInventoryDb();
        await using var pricingDb = NewPricingDb();
        await using var intakeDb = NewIntakeDb(interceptor);

        var products = new ProductRepository(catalogDb);
        var units = new UnitRepository(catalogDb);
        var categories = new CategoryRepository(catalogDb);
        var locations = new LocationRepository(catalogDb);
        var catalogFacade = new CatalogReadFacade(products, units, categories, locations);

        var createProduct = new CreateProductAdapter(products, units, categories, locations, Clock, tenant);
        var addStock = new AddStockAdapter(new ProductStockRepository(inventoryDb), catalogFacade, Clock, tenant);
        var recordPrice = new RecordPriceAdapter(
            new PriceObservationRepository(pricingDb), new UnitPriceCalculatorAdapter(units), tenant);

        var command = new CommitSessionCommand(
            sessionId, new ImportSessionRepository(intakeDb), createProduct, addStock, recordPrice, Clock, tenant);

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

        // Intake: session + line committed, with the cross-context refs recorded.
        await using var verifyIntake = NewIntakeDb();
        var committed = await verifyIntake.ImportSessions.Include(s => s.Lines).SingleAsync(s => s.Id == sessionId);
        Assert.Equal(ImportStatus.Committed, committed.Status);
        var committedLine = Assert.Single(committed.Lines);
        Assert.Equal(LineStatus.Committed, committedLine.Status);
        Assert.NotNull(committedLine.JournalId);
        Assert.NotNull(committedLine.PriceObservationId);

        // Dispatcher fired the committed event exactly once.
        var evt = Assert.Single(handler.Events);
        Assert.Equal(sessionId, evt.SessionId);
        Assert.Equal(_household, evt.HouseholdId);
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

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private InventoryDbContext NewInventoryDb()
    {
        var ctx = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private PricingDbContext NewPricingDb()
    {
        var ctx = new PricingDbContext(
            new DbContextOptionsBuilder<PricingDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private IntakeDbContext NewIntakeDb(DomainEventDispatchInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<IntakeDbContext>().UseNpgsql(db.ConnectionString);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        var ctx = new IntakeDbContext(builder.Options);
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

    private sealed class CapturingHandler : IDomainEventHandler<ImportSessionCommittedEvent>
    {
        public List<ImportSessionCommittedEvent> Events { get; } = [];

        public Task HandleAsync(ImportSessionCommittedEvent domainEvent, CancellationToken ct = default)
        {
            Events.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
