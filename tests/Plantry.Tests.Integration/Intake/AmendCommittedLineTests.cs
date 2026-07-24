using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Intake.Infrastructure;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Intake;
using Plantry.Web.Inventory;
using Plantry.Web.Pricing;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Intake;

/// <summary>
/// L3 test for <see cref="AmendCommittedLineCommand"/> wired over the REAL cross-context adapters
/// (<see cref="AmendStockAdapter"/>/<see cref="AmendPriceAdapter"/>) against a real Postgres schema
/// (ADR-023, spec A8-A10, origin plantry-x3dy). Covers the §3 worked example end to end, the A10
/// resumability re-run, and the A8 weight-priced no-touch case (spec acceptance #1/#2/#5/#7).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AmendCommittedLineTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private UnitId _lbUnitId;
    private UnitId _eachUnitId;
    private ProductId _onionsId;
    private ProductId _bananasId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalog = NewCatalogDb();
        var lb = CatalogUnit.Create(_household, "lb", "pound", Dimension.Mass, 1m, isBase: true);
        var each = CatalogUnit.Create(_household, "each", "each", Dimension.Count, 1m, isBase: true);
        await catalog.Units.AddRangeAsync(lb, each);
        var onions = Product.Create(_household, "Onions", lb.Id, Clock);
        var bananas = Product.Create(_household, "Bananas", each.Id, Clock);
        await catalog.Products.AddRangeAsync(onions, bananas);
        await catalog.SaveChangesAsync();

        _lbUnitId = lb.Id;
        _eachUnitId = each.Id;
        _onionsId = onions.Id;
        _bananasId = bananas.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Worked example (spec §3): 1 lb -> 3 lb -> 2.5 lb chains Amendment rows and supersedes price each time")]
    public async Task WorkedExample_Two_Successive_Amendments()
    {
        var tenant = new TestTenant(_household.Value);
        var (sessionId, lineId, originalObservationId) = await CommitOneLine(
            tenant, "ONIONS YELLOW 1 LB", _onionsId.Value, 1m, _lbUnitId.Value, price: 3.98m);

        // ── First amendment: 1 lb -> 3 lb ───────────────────────────────────────────────────────
        var firstResult = await RunAmend(tenant, lineId, 3m);
        Assert.True(firstResult.IsSuccess);

        await using (var verifyInventory = NewInventoryDb())
        {
            var stock = await verifyInventory.ProductStocks
                .Include(p => p.Journal).Include(p => p.Entries)
                .SingleAsync(p => p.ProductId == _onionsId.Value);
            var lot = Assert.Single(stock.Entries);
            Assert.Equal(3m, lot.Quantity); // the SAME lot, adjusted in place — never a new one (A2)
            Assert.Equal(2, stock.Journal.Count);
            var purchaseRow = Assert.Single(stock.Journal, j => j.Reason == StockReason.Purchase);
            Assert.Equal(1m, purchaseRow.Delta); // Purchase row itself is NEVER mutated
            var amendRow = Assert.Single(stock.Journal, j => j.Reason == StockReason.Amendment);
            Assert.Equal(2m, amendRow.Delta);
            Assert.Equal(StockSourceType.Intake, amendRow.SourceType);
        }

        Guid liveObservationIdAfterFirst;
        await using (var verifyPricing = NewPricingDb())
        {
            var original = await verifyPricing.PriceObservations.SingleAsync(o => o.Id == PriceObservationId.From(originalObservationId));
            Assert.NotNull(original.SupersededById); // superseded, never mutated in place (A7)
            var live = await verifyPricing.PriceObservations.SingleAsync(o => o.Id == original.SupersededById);
            Assert.Equal(3.98m, live.Price);         // same Price ...
            Assert.Equal(original.ObservedAt, live.ObservedAt); // ... and same ObservedAt (A7)
            Assert.Equal(3m, live.Quantity);
            Assert.Equal(Math.Round(3.98m / 3m, 6), live.UnitPrice); // re-derived, not naively scaled (A8); column is numeric(12,6)
            Assert.Equal(original.Id, live.AmendsId);
            liveObservationIdAfterFirst = live.Id.Value;
        }

        await using (var verifyIntake = NewIntakeDb())
        {
            var line = await verifyIntake.ImportLines.SingleAsync(l => l.Id == ImportLineId.From(lineId));
            Assert.Equal(3m, line.AmendedQuantity);
            Assert.NotNull(line.AmendedAt);
            Assert.Equal(liveObservationIdAfterFirst, line.PriceObservationId); // advanced to the live row
        }

        // ── Second amendment: 3 lb -> 2.5 lb (A3: repeats chain off the live row) ───────────────
        var secondResult = await RunAmend(tenant, lineId, 2.5m);
        Assert.True(secondResult.IsSuccess);

        await using (var verifyInventory = NewInventoryDb())
        {
            var stock = await verifyInventory.ProductStocks
                .Include(p => p.Journal).Include(p => p.Entries)
                .SingleAsync(p => p.ProductId == _onionsId.Value);
            Assert.Equal(2.5m, Assert.Single(stock.Entries).Quantity);
            Assert.Equal(3, stock.Journal.Count); // Purchase + two Amendment rows
            var amendments = stock.Journal.Where(j => j.Reason == StockReason.Amendment).OrderBy(j => j.OccurredAt).ToList();
            Assert.Equal(2, amendments.Count);
            Assert.Equal(2m, amendments[0].Delta);
            Assert.Equal(-0.5m, amendments[1].Delta);
        }

        await using (var verifyPricing = NewPricingDb())
        {
            Assert.Equal(3, await verifyPricing.PriceObservations.CountAsync());
            var liveRows = await verifyPricing.PriceObservations.Where(o => o.SupersededById == null).ToListAsync();
            var live = Assert.Single(liveRows);
            Assert.Equal(2.5m, live.Quantity);
            Assert.Equal(Math.Round(3.98m / 2.5m, 6), live.UnitPrice);
            Assert.Equal(liveObservationIdAfterFirst, live.AmendsId!.Value.Value); // chained off the FIRST amendment, not the original
        }
    }

    [Fact(DisplayName = "Resumability (A10, acceptance #7): re-running the SAME correction is a no-op on both legs")]
    public async Task Resumability_ReRun_With_The_Same_Corrected_Quantity_Does_Not_Double_Write()
    {
        var tenant = new TestTenant(_household.Value);
        var (_, lineId, _) = await CommitOneLine(
            tenant, "ONIONS YELLOW 1 LB", _onionsId.Value, 1m, _lbUnitId.Value, price: 3.98m);

        var first = await RunAmend(tenant, lineId, 3m);
        Assert.True(first.IsSuccess);

        // Re-drive with the SAME corrected quantity — as if the first attempt's caller retried after an
        // ambiguous response, or the process crashed right after this succeeded.
        var second = await RunAmend(tenant, lineId, 3m);
        Assert.True(second.IsSuccess);

        await using var verifyInventory = NewInventoryDb();
        var stock = await verifyInventory.ProductStocks
            .Include(p => p.Journal).Include(p => p.Entries)
            .SingleAsync(p => p.ProductId == _onionsId.Value);
        Assert.Equal(3m, Assert.Single(stock.Entries).Quantity);
        Assert.Equal(2, stock.Journal.Count); // Purchase + exactly ONE Amendment row — the retry wrote nothing new
        Assert.Single(stock.Journal, j => j.Reason == StockReason.Amendment);

        await using var verifyPricing = NewPricingDb();
        Assert.Equal(2, await verifyPricing.PriceObservations.CountAsync()); // original + ONE amending row, not two
        var liveRows = await verifyPricing.PriceObservations.Where(o => o.SupersededById == null).ToListAsync();
        var live = Assert.Single(liveRows);
        Assert.Equal(3m, live.Quantity);
    }

    [Fact(DisplayName = "Weight-priced line (spec acceptance #5, A8): an each-count fix leaves the weight-denominated observation untouched")]
    public async Task WeightPricedLine_EachCount_Amendment_Leaves_Price_Untouched()
    {
        var tenant = new TestTenant(_household.Value);

        Guid sessionId, lineId;
        Guid stockEntryId;
        Guid priceObservationId;
        await using (var intakeDb = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            var line = session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, """{"x":1}""",
                suggestedProductId: _bananasId.Value, suggestedQuantity: 1.34m, suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
                receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
                estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);
            session.MarkReady("Superstore", Clock.UtcNow);
            line.Confirm(_bananasId.Value, skuId: null, 7m, _eachUnitId.Value, _locationId, expiryDate: null, price: 0.79m);
            await intakeDb.ImportSessions.AddAsync(session);
            await intakeDb.SaveChangesAsync();
            sessionId = session.Id.Value;
            lineId = line.Id.Value;
        }

        var (commitLineId, resultOk) = await CommitSession(tenant, sessionId);
        Assert.True(resultOk);
        Assert.Equal(lineId, commitLineId);

        await using (var verifyIntake0 = NewIntakeDb())
        {
            var line = await verifyIntake0.ImportLines.SingleAsync(l => l.Id == ImportLineId.From(lineId));
            stockEntryId = line.JournalId!.Value;
            priceObservationId = line.PriceObservationId!.Value;
        }
        await using (var verifyPricing0 = NewPricingDb())
        {
            var obs = await verifyPricing0.PriceObservations.SingleAsync(o => o.Id == PriceObservationId.From(priceObservationId));
            Assert.Equal(1.34m, obs.Quantity);       // recorded in the receipt's TRUE weight, not the each-count
            Assert.Equal(_lbUnitId.Value, obs.UnitId);
        }

        // Amend the each-count 7 -> 9 (the committed, correctable quantity).
        var amendResult = await RunAmend(tenant, lineId, 9m);
        Assert.True(amendResult.IsSuccess);

        await using (var verifyInventory = NewInventoryDb())
        {
            var stock = await verifyInventory.ProductStocks
                .Include(p => p.Journal).Include(p => p.Entries)
                .SingleAsync(p => p.ProductId == _bananasId.Value);
            Assert.Equal(9m, Assert.Single(stock.Entries).Quantity); // stock DOES amend the each-count
            Assert.Single(stock.Journal, j => j.Reason == StockReason.Amendment);
        }

        await using (var verifyPricing = NewPricingDb())
        {
            // Still exactly one observation — never superseded, never touched.
            var obs = Assert.Single(await verifyPricing.PriceObservations.ToListAsync());
            Assert.Null(obs.SupersededById);
            Assert.Equal(1.34m, obs.Quantity);
            Assert.Equal(_lbUnitId.Value, obs.UnitId);
            Assert.Equal(0.79m, obs.Price);
        }

        await using (var verifyIntake = NewIntakeDb())
        {
            var line = await verifyIntake.ImportLines.SingleAsync(l => l.Id == ImportLineId.From(lineId));
            Assert.Equal(9m, line.AmendedQuantity);
            Assert.Equal(priceObservationId, line.PriceObservationId); // untouched (A8)
        }
    }

    // ── Shared setup / SUT wiring ────────────────────────────────────────────────────────────────

    private async Task<(Guid SessionId, Guid LineId, Guid PriceObservationId)> CommitOneLine(
        ITenantContext tenant, string receiptText, Guid productId, decimal quantity, Guid unitId, decimal price)
    {
        Guid sessionId, lineId;
        await using (var setup = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            var line = session.AddLine(1, receiptText, SuggestedConfidence.High, """{"x":1}""");
            session.MarkReady("Superstore", Clock.UtcNow);
            line.Confirm(productId, skuId: null, quantity, unitId, _locationId, expiryDate: null, price: price);
            await setup.ImportSessions.AddAsync(session);
            await setup.SaveChangesAsync();
            sessionId = session.Id.Value;
            lineId = line.Id.Value;
        }

        var (_, ok) = await CommitSession(tenant, sessionId);
        Assert.True(ok);

        await using var verifyIntake = NewIntakeDb();
        var committedLine = await verifyIntake.ImportLines.SingleAsync(l => l.Id == ImportLineId.From(lineId));
        return (sessionId, lineId, committedLine.PriceObservationId!.Value);
    }

    /// <summary>Runs the real <see cref="CommitSessionCommand"/> (mirrors <c>IntakeCommitTests</c>) so the
    /// amendment tests exercise a genuinely committed line, not a hand-seeded one. Weight-unit resolution
    /// (a weight-priced line's "lb" receipt label) runs for real too — the seeded Catalog unit's Code
    /// matches the label, so <see cref="ReviewReferenceDataProvider"/> resolves it without any test-side help.</summary>
    private async Task<(Guid LineId, bool Ok)> CommitSession(ITenantContext tenant, Guid sessionId)
    {
        await using var catalogDb = NewCatalogDb();
        await using var inventoryDb = NewInventoryDb();
        await using var pricingDb = NewPricingDb();
        await using var intakeDb = NewIntakeDb();

        var products = new ProductRepository(catalogDb);
        var units = new UnitRepository(catalogDb);
        var categories = new CategoryRepository(catalogDb);
        var locations = new LocationRepository(catalogDb);
        var catalogFacade = new CatalogReadFacade(products, units, categories, locations);

        var createProduct = new CreateProductAdapter(products, units, categories, locations, Clock, tenant);
        var addStock = new AddStockAdapter(new ProductStockRepository(inventoryDb), catalogFacade, Clock, tenant);
        var recordPrice = new RecordPriceAdapter(
            new PriceObservationRepository(pricingDb), new UnitPriceCalculatorAdapter(units), tenant,
            NullLogger<RecordObservationCommand>.Instance);
        var ensureStore = new EnsurePurchaseStoreAdapter(new StoreRepository(catalogDb), tenant, Clock);
        var referenceData = new ReviewReferenceDataProvider(products, units, locations, categories, new StoreRepository(catalogDb));
        var seedConversion = new SeedConversionAdapter(products, Clock, NullLogger<SeedConversionAdapter>.Instance);

        var command = new CommitSessionCommand(
            ImportSessionId.From(sessionId), new ImportSessionRepository(intakeDb), createProduct, addStock,
            recordPrice, ensureStore, referenceData, seedConversion, Clock, tenant,
            NullLogger<CommitSessionCommand>.Instance);

        var result = await command.ExecuteAsync();

        // Fresh context — a genuine round trip, not the same tracked instance the command just wrote through.
        await using var verify = NewIntakeDb();
        var session = await verify.ImportSessions.Include(s => s.Lines).SingleAsync(s => s.Id == ImportSessionId.From(sessionId));
        var line = session.Lines.Single();
        return (line.Id.Value, result.IsSuccess);
    }

    private async Task<Result> RunAmend(ITenantContext tenant, Guid lineId, decimal correctedQuantity)
    {
        await using var catalogDb = NewCatalogDb();
        await using var inventoryDb = NewInventoryDb();
        await using var pricingDb = NewPricingDb();
        await using var intakeDb = NewIntakeDb();

        var amendStock = new AmendStockAdapter(new ProductStockRepository(inventoryDb), Clock, tenant, NullLogger<AmendPurchaseCommand>.Instance);
        var amendPrice = new AmendPriceAdapter(
            new PriceObservationRepository(pricingDb), new UnitPriceCalculatorAdapter(new UnitRepository(catalogDb)),
            tenant, NullLogger<RecordAmendedObservationCommand>.Instance);

        var command = new AmendCommittedLineCommand(
            lineId, correctedQuantity, _userId, new ImportSessionRepository(intakeDb), amendStock, amendPrice,
            Clock, tenant, NullLogger<AmendCommittedLineCommand>.Instance);

        return await command.ExecuteAsync();
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

    private IntakeDbContext NewIntakeDb()
    {
        var ctx = new IntakeDbContext(
            new DbContextOptionsBuilder<IntakeDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class TestTenant(Guid household) : ITenantContext
    {
        public Guid? HouseholdId { get; } = household;
    }
}
