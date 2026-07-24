using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// L3 integration tests for the seeded "serving" count unit (plantry-n1za, recipe-composition.md §9):
/// the seeder must add a <c>srv</c>/serving Count unit (factor 1, UnitSystem.Unspecified) to every new
/// household, and the AddServingUnit data migration must backfill it onto every household seeded before
/// this feature — unconditionally, and idempotently (a second run inserts nothing new).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ServingUnitTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Seeder adds a 'srv'/serving Count unit (factor 1, Unspecified) to a new household")]
    public async Task Seeder_Adds_ServingUnit()
    {
        await using (var seedDb = NewCatalogDb())
        {
            await new CatalogReferenceDataSeeder(seedDb).SeedAsync(_household);
        }

        await using var db2 = NewCatalogDb();
        var serving = await db2.Units.SingleAsync(u => u.Code == "srv");

        Assert.Equal("serving", serving.Name);
        Assert.Equal(Dimension.Count, serving.Dimension);
        Assert.Equal(1m, serving.FactorToBase);
        Assert.False(serving.IsBase);
        Assert.Equal(UnitSystem.Unspecified, serving.UnitSystem);
    }

    [Fact(DisplayName = "Backfill adds 'srv' unconditionally to a household seeded before this feature")]
    public async Task DataMigration_Backfills_ServingUnit_Unconditionally()
    {
        // Simulate a pre-feature household: seed it, then delete the srv row it would not have had.
        await using (var seedDb = NewCatalogDb())
        {
            await new CatalogReferenceDataSeeder(seedDb).SeedAsync(_household);
        }
        await using (var db1 = NewCatalogDb())
        {
            await db1.Database.ExecuteSqlRawAsync(
                "DELETE FROM catalog.units WHERE household_id = {0} AND lower(symbol) = 'srv'",
                _household.Value);
        }

        await ReplayBackfillAsync();

        await using var db2 = NewCatalogDb();
        var serving = await db2.Units.SingleAsync(u => u.Code == "srv");
        Assert.Equal("serving", serving.Name);
        Assert.Equal(Dimension.Count, serving.Dimension);
        Assert.Equal(1m, serving.FactorToBase);
        Assert.Equal(UnitSystem.Unspecified, serving.UnitSystem);
    }

    [Fact(DisplayName = "Backfill is idempotent — re-running it does not duplicate the serving unit")]
    public async Task DataMigration_Backfill_IsIdempotent()
    {
        await using (var seedDb = NewCatalogDb())
        {
            await new CatalogReferenceDataSeeder(seedDb).SeedAsync(_household);
        }
        await using (var db1 = NewCatalogDb())
        {
            await db1.Database.ExecuteSqlRawAsync(
                "DELETE FROM catalog.units WHERE household_id = {0} AND lower(symbol) = 'srv'",
                _household.Value);
        }

        // Replay the backfill twice — mirrors a migration being run, then re-run (e.g. a second
        // household onboarded, or an operator re-applying migrations).
        await ReplayBackfillAsync();
        await ReplayBackfillAsync();

        await using var db2 = NewCatalogDb();
        var count = await db2.Units.CountAsync(u => u.HouseholdId == _household && u.Code == "srv");
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Replays the AddServingUnit migration's Up() SQL directly (mirrors UnitSystemTests' pattern of
    /// replaying AddUnitSystem's backfill statements against a live fixture, rather than exercising
    /// EF's full migration runner against the shared test DB).
    /// </summary>
    private async Task ReplayBackfillAsync()
    {
        await using var db1 = NewCatalogDb();
        await db1.Database.ExecuteSqlRawAsync(
            "INSERT INTO catalog.units " +
            "(id, household_id, symbol, name, dimension, factor_to_base, is_base, display_style, unit_system) " +
            "SELECT gen_random_uuid(), h.household_id, 'srv', 'serving', 'count', 1, false, 'decimal', 'unspecified' " +
            "FROM (SELECT DISTINCT household_id FROM catalog.units) h " +
            "WHERE NOT EXISTS ( " +
            "    SELECT 1 FROM catalog.units u " +
            "    WHERE u.household_id = h.household_id AND lower(u.symbol) = 'srv' " +
            ");");
    }

    private DbContextOptions<CatalogDbContext> CatalogOptions() =>
        new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(CatalogOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
