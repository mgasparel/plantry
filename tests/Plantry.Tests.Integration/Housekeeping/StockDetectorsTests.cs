using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Composition.Infrastructure;
using Plantry.Web.Housekeeping;
using Xunit;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Integration.Housekeeping;

/// <summary>
/// L3 contract/integration tests for the stock-family Tidy Up detectors (D1 <see cref="StockUnitUnconvertibleDetector"/>,
/// D3 <see cref="StockExpiredDetector"/>, D4 <see cref="StapleNoLowStockAlertDetector"/>, D6
/// <see cref="MixedIncompatibleUnitsDetector"/>) against the real migrated schema, replacing the retired
/// fake-port unit tests now that ADR-021/ADR-024 Phase A moved these detectors onto
/// <see cref="IStockFactsReadModel"/>'s raw cross-schema SQL. Mirrors <c>MealPlanWeekReadModelTests</c>'
/// seeding conventions. RLS isolation is proven separately in <see cref="StockFactsReadModelRlsIsolationTests"/>.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StockDetectorsTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = new FixedClock(new DateOnly(2026, 7, 22));
    private HouseholdId _household;
    private Guid _gramsId;
    private Guid _eachId;
    private Guid _packId;
    private int _entrySequence = 100;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.From(Guid.Parse("00000000-0000-0000-0000-000000000051"));
        _entrySequence = 100;

        await using var catalog = NewCatalogDb(_household);
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var each = CatalogUnit.Create(_household, "ea", "each", Dimension.Count, 1m);
        var pack = CatalogUnit.Create(_household, "pk", "pack", Dimension.Count, 1m);
        await catalog.Units.AddRangeAsync(grams, each, pack);
        await catalog.SaveChangesAsync();
        _gramsId = grams.Id.Value;
        _eachId = each.Id.Value;
        _packId = pack.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── D1: StockUnitUnconvertibleDetector ──────────────────────────────────────────────────────

    [Fact(DisplayName = "D1: an active lot in a unit unconvertible to the default unit — produces a finding")]
    public async Task D1_UnconvertibleLot_ProducesFinding()
    {
        var productId = await SeedProductAsync("Cereal", _gramsId);
        var locationId = await SeedLocationAsync("Pantry");
        await SeedStockEntryAsync(productId, locationId, 3m, _eachId, expiryDate: null);

        var findings = await BuildD1().DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.StockUnitUnconvertible, finding.DetectorId);
        Assert.Equal(productId, finding.SubjectId);
        Assert.Equal("Cereal", finding.SubjectName);
        Assert.Equal("3 ea in stock, display unit is g", finding.Specifics);
    }

    [Fact(DisplayName = "D1: a lot in the product's own default unit — no finding")]
    public async Task D1_ConvertibleLot_NoFinding()
    {
        var productId = await SeedProductAsync("Flour", _gramsId);
        var locationId = await SeedLocationAsync("Pantry2");
        await SeedStockEntryAsync(productId, locationId, 500m, _gramsId, expiryDate: null);

        var findings = await BuildD1().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D1: fingerprint pinning — a quantity-only change does not change the fingerprint")]
    public async Task D1_Fingerprint_UnaffectedByQuantity()
    {
        var productId = await SeedProductAsync("Rice", _gramsId);
        var locationId = await SeedLocationAsync("Pantry3");
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null);
        var before = Assert.Single(await BuildD1().DetectAsync());

        await SeedStockEntryAsync(productId, locationId, 4m, _eachId, expiryDate: null);
        var after = Assert.Single(await BuildD1().DetectAsync());

        Assert.Equal(before.FactsFingerprint, after.FactsFingerprint);
    }

    [Fact(DisplayName = "D1: no household in tenant context — returns no findings")]
    public async Task D1_NoTenant_ReturnsEmpty()
    {
        var productId = await SeedProductAsync("Sugar", _gramsId);
        var locationId = await SeedLocationAsync("Pantry4");
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null);

        var findings = await BuildD1(tenant: new TenantContext()).DetectAsync();

        Assert.Empty(findings);
    }

    // ── D3: StockExpiredDetector ─────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "D3: an active lot expired before today — produces a finding")]
    public async Task D3_ExpiredLot_ProducesFinding()
    {
        var productId = await SeedProductAsync("Yogurt", _eachId);
        var locationId = await SeedLocationAsync("Fridge");
        await SeedStockEntryAsync(productId, locationId, 2m, _eachId, expiryDate: new DateOnly(2026, 7, 1));

        var findings = await BuildD3().DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.StockExpired, finding.DetectorId);
        Assert.Equal("1 lot expired 2026-07-01", finding.Specifics);
    }

    [Fact(DisplayName = "D3: a lot expiring exactly today — 0-day grace window, does not fire")]
    public async Task D3_ExpiresToday_DoesNotFire()
    {
        var productId = await SeedProductAsync("Milk", _eachId);
        var locationId = await SeedLocationAsync("Fridge2");
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: new DateOnly(2026, 7, 22));

        var findings = await BuildD3().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D3: a depleted lot past expiry — inactive, does not fire")]
    public async Task D3_DepletedExpiredLot_DoesNotFire()
    {
        var productId = await SeedProductAsync("Cheese", _eachId);
        var locationId = await SeedLocationAsync("Fridge3");
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: new DateOnly(2026, 7, 1), depleted: true);

        var findings = await BuildD3().DetectAsync();

        Assert.Empty(findings);
    }

    // ── D4: StapleNoLowStockAlertDetector ────────────────────────────────────────────────────────

    [Fact(DisplayName = "D4: 3 distinct purchase dates within 90 days, no threshold — produces a finding")]
    public async Task D4_ThreeDistinctPurchaseDates_ProducesFinding()
    {
        var productId = await SeedProductAsync("Bananas", _eachId);
        var locationId = await SeedLocationAsync("Counter");
        var today = new DateOnly(2026, 7, 22);
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-1));
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-20));
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-40));

        var findings = await BuildD4().DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.StapleNoLowStockAlert, finding.DetectorId);
        Assert.Equal(productId, finding.SubjectId);
    }

    [Fact(DisplayName = "D4: only 2 distinct purchase dates — does not fire")]
    public async Task D4_TwoDistinctPurchaseDates_DoesNotFire()
    {
        var productId = await SeedProductAsync("Apples", _eachId);
        var locationId = await SeedLocationAsync("Counter2");
        var today = new DateOnly(2026, 7, 22);
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-1));
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-20));

        var findings = await BuildD4().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D4: threshold already set — never flagged even with frequent purchases")]
    public async Task D4_ThresholdSet_NeverFlagged()
    {
        var productId = await SeedProductAsync("Eggs", _eachId);
        var locationId = await SeedLocationAsync("Counter3");
        var today = new DateOnly(2026, 7, 22);
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-1));
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-20));
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-40));
        await SetLowStockThresholdAsync(productId, 1m);

        var findings = await BuildD4().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D4: depleted entries still count toward purchase frequency")]
    public async Task D4_DepletedEntries_StillCount()
    {
        var productId = await SeedProductAsync("Butter", _eachId);
        var locationId = await SeedLocationAsync("Counter4");
        var today = new DateOnly(2026, 7, 22);
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-1), depleted: true);
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-20), depleted: true);
        await SeedStockEntryAsync(productId, locationId, 1m, _eachId, expiryDate: null, purchasedAt: today.AddDays(-40));

        var findings = await BuildD4().DetectAsync();

        Assert.Single(findings);
    }

    // ── D6: MixedIncompatibleUnitsDetector ───────────────────────────────────────────────────────

    [Fact(DisplayName = "D6: two active-lot units, neither convertible to the default — fires (falls back to \"?\")")]
    public async Task D6_TwoIncompatibleUnits_Fires()
    {
        var productId = await SeedProductAsync("Onions", _gramsId);
        var locationId = await SeedLocationAsync("Bin");
        await SeedStockEntryAsync(productId, locationId, 2m, _eachId, expiryDate: null);
        await SeedStockEntryAsync(productId, locationId, 3m, _packId, expiryDate: null);

        var findings = await BuildD6().DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.StockMixedIncompatibleUnits, finding.DetectorId);
        Assert.Equal(productId, finding.SubjectId);
    }

    [Fact(DisplayName = "D6: a single unconvertible unit — falls back to the lot's own unit, not \"?\" — no finding")]
    public async Task D6_SingleIncompatibleUnit_NoFinding()
    {
        var productId = await SeedProductAsync("Garlic", _gramsId);
        var locationId = await SeedLocationAsync("Bin2");
        await SeedStockEntryAsync(productId, locationId, 5m, _eachId, expiryDate: null);

        var findings = await BuildD6().DetectAsync();

        Assert.Empty(findings);
    }

    // ── D8: MissingDefaultLocationDetector ────────────────────────────────────────────────────────

    [Fact(DisplayName = "D8: catalog-only tracked concrete product without default location produces finding")]
    public async Task D8_CatalogOnlyTrackedConcrete_ProducesFinding()
    {
        var productId = await SeedProductAsync("Catalog-only D8", _eachId);

        var findings = await BuildD8().DetectAsync();

        var finding = Assert.Single(findings.Where(x => x.SubjectId == productId));
        Assert.Equal(DetectorId.ProductMissingDefaultLocation, finding.DetectorId);
        Assert.Equal("Catalog-only D8", finding.SubjectName);
    }

    [Fact(DisplayName = "D8: default location, untracked, and parent products are excluded while variants remain eligible")]
    public async Task D8_FilteringRules_ArePreserved()
    {
        var defaultedId = await SeedProductAsync("D8 Defaulted", _eachId);
        await SetProductDefaultLocationAsync(defaultedId);
        var untrackedId = await SeedProductAsync("D8 Untracked", _eachId, trackStock: false);
        var parentId = await SeedProductAsync("D8 Parent", _eachId);
        await SetProductHasVariantsAsync(parentId, true);
        var variantId = await SeedProductAsync("D8 Variant", _eachId);

        var findings = await BuildD8().DetectAsync();

        Assert.DoesNotContain(findings, x => x.SubjectId == defaultedId);
        Assert.DoesNotContain(findings, x => x.SubjectId == untrackedId);
        Assert.DoesNotContain(findings, x => x.SubjectId == parentId);
        Assert.Contains(findings, x => x.SubjectId == variantId);
    }

    [Fact(DisplayName = "D8: physical lot location does not substitute for missing product default")]
    public async Task D8_PhysicalLotStillProducesFinding()
    {
        var productId = await SeedProductAsync("D8 Physical lot", _eachId);
        var locationId = await SeedLocationAsync("D8 Freezer");
        await SeedStockEntryAsync(productId, locationId, 2m, _eachId, expiryDate: null);

        var finding = Assert.Single((await BuildD8().DetectAsync()).Where(x => x.SubjectId == productId));
        Assert.Equal("Default location not set", finding.Specifics);
    }

    [Fact(DisplayName = "D8: archived product is excluded")]
    public async Task D8_ArchivedProduct_IsExcluded()
    {
        var productId = await SeedProductAsync("D8 Archived", _eachId);
        await using (var catalog = NewCatalogDb(_household))
        {
            var product = await catalog.Products.SingleAsync(x => x.Id == ProductId.From(productId));
            product.Archive(Clock);
            await catalog.SaveChangesAsync();
        }

        var findings = await BuildD8().DetectAsync();

        Assert.DoesNotContain(findings, x => x.SubjectId == productId);
    }

    private MissingDefaultLocationDetector BuildD8(ITenantContext? tenant = null) =>
        new(NewStockFactsReadModel(tenant), tenant ?? TenantFor(_household));

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private StockUnitUnconvertibleDetector BuildD1(ITenantContext? tenant = null) =>
        new(NewStockFactsReadModel(tenant), tenant ?? TenantFor(_household));

    private StockExpiredDetector BuildD3(ITenantContext? tenant = null) =>
        new(NewStockFactsReadModel(tenant), Clock, tenant ?? TenantFor(_household));

    private StapleNoLowStockAlertDetector BuildD4(ITenantContext? tenant = null) =>
        new(NewStockFactsReadModel(tenant), Clock, tenant ?? TenantFor(_household));

    private MixedIncompatibleUnitsDetector BuildD6(ITenantContext? tenant = null) =>
        new(NewStockFactsReadModel(tenant), tenant ?? TenantFor(_household));

    private IStockFactsReadModel NewStockFactsReadModel(ITenantContext? tenant) =>
        new StockFactsReadModel(db.ConnectionString, tenant ?? TenantFor(_household));

    private static ITenantContext TenantFor(HouseholdId household)
    {
        var tenant = new TenantContext();
        tenant.Set(household.Value);
        return tenant;
    }

    private PantryDbContext NewCatalogDb(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new PantryDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private async Task<Guid> SeedProductAsync(string name, Guid defaultUnitId, bool trackStock = true)
    {
        await using var catalog = NewCatalogDb(_household);
        var product = Product.Create(_household, name, UnitId.From(defaultUnitId), Clock, trackStock: trackStock);
        await catalog.Products.AddAsync(product);
        await catalog.SaveChangesAsync();
        return product.Id.Value;
    }

    private async Task SetProductDefaultLocationAsync(Guid productId)
    {
        var locationId = await SeedLocationAsync("D8 Default");
        await using var catalog = NewCatalogDb(_household);
        var product = await catalog.Products.SingleAsync(x => x.Id == ProductId.From(productId));
        product.SetDefaultLocation(LocationId.From(locationId), Clock);
        await catalog.SaveChangesAsync();
    }

    private async Task SetProductHasVariantsAsync(Guid productId, bool hasVariants)
    {
        await using var catalog = NewCatalogDb(_household);
        var product = await catalog.Products.SingleAsync(x => x.Id == ProductId.From(productId));
        product.SetHasVariants(hasVariants, Clock);
        await catalog.SaveChangesAsync();
    }

    private async Task<Guid> SeedLocationAsync(string name)
    {
        await using var catalog = NewCatalogDb(_household);
        var location = Location.Create(_household, name, LocationType.Ambient);
        await catalog.Locations.AddAsync(location);
        await catalog.SaveChangesAsync();
        return location.Id.Value;
    }

    private async Task EnsureProductStockAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.product_stock (household_id, product_id, created_at, updated_at)
            VALUES (@hid, @pid, NOW(), NOW())
            ON CONFLICT (household_id, product_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedStockEntryAsync(
        Guid productId, Guid locationId, decimal quantity, Guid unitId, DateOnly? expiryDate,
        DateOnly? purchasedAt = null, bool depleted = false)
    {
        await EnsureProductStockAsync(productId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        var depletedAt = depleted ? (object)new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc) : DBNull.Value;
        var expiryObj = expiryDate.HasValue ? (object)expiryDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        var purchasedObj = purchasedAt.HasValue ? (object)purchasedAt.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

        cmd.CommandText = """
            INSERT INTO inventory.stock_entry
                (entry_id, household_id, product_id, location_id, quantity, unit_id, expiry_date,
                 is_open, created_at, updated_at, depleted_at, purchased_at)
            VALUES
                (@id, @hid, @pid, @lid, @qty, @uid, @exp,
                 false, NOW(), NOW(), @dep, @purch)
            """;
        cmd.Parameters.AddWithValue("id", Guid.Parse($"00000000-0000-0000-0000-{_entrySequence++:000000000000}"));
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("lid", locationId);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("exp", expiryObj);
        cmd.Parameters.AddWithValue("dep", depletedAt);
        cmd.Parameters.AddWithValue("purch", purchasedObj);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SetLowStockThresholdAsync(Guid productId, decimal threshold)
    {
        await EnsureProductStockAsync(productId);
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE inventory.product_stock SET low_stock_threshold = @threshold
            WHERE household_id = @hid AND product_id = @pid
            """;
        cmd.Parameters.AddWithValue("threshold", threshold);
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        await cmd.ExecuteNonQueryAsync();
    }
}
