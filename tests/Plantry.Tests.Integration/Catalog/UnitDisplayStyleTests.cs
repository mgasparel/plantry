using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// L3 integration tests for Unit.DisplayStyle (quantity-display.md Q2/Q10): the AddUnitDisplayStyle
/// migration must apply clean, the enum must round-trip as text with a CHECK backstop, the column
/// must default to 'decimal', the reference-data seeder must mark cup/tbsp/tsp Fraction for new
/// households, and the one-time data migration's WHERE clause must flip the same legacy units.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class UnitDisplayStyleTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "A Fraction-styled unit round-trips through EF")]
    public async Task DisplayStyle_RoundTrips_Through_EfMapping()
    {
        UnitId cupId;
        await using (var db1 = NewCatalogDb())
        {
            var cup = CatalogUnit.Create(_household, "cup", "cup", Dimension.Volume, 240m);
            cup.SetDisplayStyle(DisplayStyle.Fraction);
            var gram = CatalogUnit.Create(_household, "g", "gram", Dimension.Mass, 1m, isBase: true);
            await db1.Units.AddRangeAsync(cup, gram);
            await db1.SaveChangesAsync();
            cupId = cup.Id;
        }

        await using var db2 = NewCatalogDb();
        var loadedCup = await db2.Units.SingleAsync(u => u.Id == cupId);
        var loadedGram = await db2.Units.SingleAsync(u => u.Code == "g");

        Assert.Equal(DisplayStyle.Fraction, loadedCup.DisplayStyle);
        Assert.Equal(DisplayStyle.Decimal, loadedGram.DisplayStyle);
    }

    [Fact(DisplayName = "Migration defaults a column-less unit row to 'decimal'")]
    public async Task Migration_Defaults_Existing_Rows_To_Decimal()
    {
        // Simulate a pre-migration row: insert omitting display_style entirely. The migration's
        // column default (defaultValue: "decimal") is what an existing row would have received.
        await using var db1 = NewCatalogDb();
        await db1.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.units (id, household_id, symbol, name, dimension, factor_to_base, is_base)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6})
            """,
            Guid.CreateVersion7(), _household.Value, "g", "gram", "mass", 1m, true);

        await using var db2 = NewCatalogDb();
        var loaded = await db2.Units.SingleAsync(u => u.Code == "g");
        Assert.Equal(DisplayStyle.Decimal, loaded.DisplayStyle);
    }

    [Fact(DisplayName = "CHECK constraint rejects an unknown display_style value")]
    public async Task CheckConstraint_Rejects_Unknown_DisplayStyle_Value()
    {
        await using var db1 = NewCatalogDb();

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db1.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.units (id, household_id, symbol, name, dimension, factor_to_base, is_base, display_style)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
            """,
            Guid.CreateVersion7(), _household.Value, "g", "gram", "mass", 1m, true, "nonsense"));

        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact(DisplayName = "Seeder marks cup/tbsp/tsp Fraction and everything else Decimal for a new household")]
    public async Task Seeder_Marks_Scoop_Units_Fraction_For_New_Household()
    {
        await using (var seedDb = NewCatalogDb())
        {
            await new CatalogReferenceDataSeeder(seedDb).SeedAsync(_household);
        }

        await using var db2 = NewCatalogDb();
        var units = await db2.Units.ToListAsync();

        var fraction = units.Where(u => u.DisplayStyle == DisplayStyle.Fraction)
            .Select(u => u.Code).OrderBy(c => c).ToList();
        Assert.Equal(new[] { "cup", "tbsp", "tsp" }, fraction);

        // Every other seeded unit (g, kg, ml, l, oz, ea, …) keeps the Decimal default.
        Assert.All(units.Where(u => u.Code is not ("cup" or "tbsp" or "tsp")),
            u => Assert.Equal(DisplayStyle.Decimal, u.DisplayStyle));
    }

    [Fact(DisplayName = "Data-migration WHERE clause flips legacy cup/tbsp/tsp (case-insensitive), leaving others Decimal")]
    public async Task DataMigration_Flips_Legacy_Scoop_Units_Only()
    {
        // Insert 'decimal' rows as a pre-feature household would have had them, then replay the
        // migration's one-time UPDATE. Uppercase 'CUP' proves the case-insensitive match; 'ml' proves
        // a same-dimension non-scoop unit is left untouched.
        await using var db1 = NewCatalogDb();
        foreach (var (code, dimension, factor) in new[]
                 {
                     ("CUP", "volume", 240m),
                     ("tbsp", "volume", 14.7868m),
                     ("tsp", "volume", 4.92892m),
                     ("ml", "volume", 1m),
                     ("g", "mass", 1m),
                 })
        {
            await db1.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO catalog.units (id, household_id, symbol, name, dimension, factor_to_base, is_base, display_style)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, 'decimal')
                """,
                Guid.CreateVersion7(), _household.Value, code, code, dimension, factor, false);
        }

        await db1.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.units SET display_style = 'fraction' WHERE lower(symbol) IN ('cup', 'tbsp', 'tsp');");

        await using var db2 = NewCatalogDb();
        var byCode = await db2.Units.ToDictionaryAsync(u => u.Code, u => u.DisplayStyle);

        Assert.Equal(DisplayStyle.Fraction, byCode["CUP"]);
        Assert.Equal(DisplayStyle.Fraction, byCode["tbsp"]);
        Assert.Equal(DisplayStyle.Fraction, byCode["tsp"]);
        Assert.Equal(DisplayStyle.Decimal, byCode["ml"]);
        Assert.Equal(DisplayStyle.Decimal, byCode["g"]);
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
