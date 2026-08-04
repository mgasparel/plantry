using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Housekeeping;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Housekeeping;

/// <summary>
/// L3 RLS isolation test for <see cref="StockFactsReadModel"/> (ADR-021 rule 4): two households, one
/// read model, no leakage — proves the household-wide facts load (no caller-supplied id set to narrow
/// by, unlike <c>MealPlanWeekReadModel</c>) is still Postgres-RLS-isolated on the raw
/// <c>inventory.product_stock</c>/<c>inventory.stock_entry</c> tables it scans without a WHERE
/// household_id clause of its own.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StockFactsReadModelRlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = new FixedClock(new DateOnly(2026, 7, 22));
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private Guid _productA;
    private Guid _productB;
    private Guid _gramsId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();

        // Units are per-household rows too — seed one grams unit per household.
        _gramsId = await SeedUnitAsync(_householdA, "g");
        var gramsB = await SeedUnitAsync(_householdB, "g");

        _productA = await SeedProductAsync(_householdA, "Household A Product", _gramsId);
        _productB = await SeedProductAsync(_householdB, "Household B Product", gramsB);

        var locationA = await SeedLocationAsync(_householdA, "A Pantry");
        var locationB = await SeedLocationAsync(_householdB, "B Pantry");

        await SeedStockEntryAsync(_householdA, _productA, locationA, _gramsId);
        await SeedStockEntryAsync(_householdB, _productB, locationB, gramsB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Household A's read model never sees household B's products or stock")]
    public async Task LoadAsync_HouseholdA_DoesNotSee_HouseholdB()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);
        var rm = new StockFactsReadModel(db.AppUserConnectionString, tenant);

        var bag = await rm.LoadAsync();

        Assert.Contains(_productA, bag.Products.Keys);
        Assert.DoesNotContain(_productB, bag.Products.Keys);
        Assert.Contains(_productA, bag.StockByProduct.Keys);
        Assert.DoesNotContain(_productB, bag.StockByProduct.Keys);
    }

    [Fact(DisplayName = "No tenant set — the RLS-armed connection returns no rows for either household")]
    public async Task LoadAsync_NoTenant_ReturnsNoStock()
    {
        var tenant = new TenantContext(); // never set
        var rm = new StockFactsReadModel(db.AppUserConnectionString, tenant);

        var bag = await rm.LoadAsync();

        Assert.Empty(bag.StockByProduct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private CatalogDbContext NewCatalogDb(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new CatalogDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private async Task<Guid> SeedUnitAsync(HouseholdId household, string code)
    {
        await using var catalog = NewCatalogDb(household);
        var unit = CatalogUnit.Create(household, code, code, Dimension.Mass, 1m, isBase: true);
        await catalog.Units.AddAsync(unit);
        await catalog.SaveChangesAsync();
        return unit.Id.Value;
    }

    private async Task<Guid> SeedProductAsync(HouseholdId household, string name, Guid defaultUnitId)
    {
        await using var catalog = NewCatalogDb(household);
        var product = Product.Create(household, name, UnitId.From(defaultUnitId), Clock);
        await catalog.Products.AddAsync(product);
        await catalog.SaveChangesAsync();
        return product.Id.Value;
    }

    private async Task<Guid> SeedLocationAsync(HouseholdId household, string name)
    {
        await using var catalog = NewCatalogDb(household);
        var location = Location.Create(household, name, LocationType.Ambient);
        await catalog.Locations.AddAsync(location);
        await catalog.SaveChangesAsync();
        return location.Id.Value;
    }

    private async Task SeedStockEntryAsync(HouseholdId household, Guid productId, Guid locationId, Guid unitId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using (var psCmd = conn.CreateCommand())
        {
            psCmd.CommandText = """
                INSERT INTO inventory.product_stock (household_id, product_id, created_at, updated_at)
                VALUES (@hid, @pid, NOW(), NOW())
                ON CONFLICT (household_id, product_id) DO NOTHING
                """;
            psCmd.Parameters.AddWithValue("hid", household.Value);
            psCmd.Parameters.AddWithValue("pid", productId);
            await psCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.stock_entry
                (entry_id, household_id, product_id, location_id, quantity, unit_id, is_open, created_at, updated_at)
            VALUES
                (@id, @hid, @pid, @lid, 1, @uid, false, NOW(), NOW())
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("lid", locationId);
        cmd.Parameters.AddWithValue("uid", unitId);
        await cmd.ExecuteNonQueryAsync();
    }
}
