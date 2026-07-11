using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// L3 integration tests for Unit.UnitSystem (quantity-display.md Q5, amended 2026-07-11): the
/// AddUnitSystem migration must apply clean, the enum must round-trip as text with a CHECK backstop,
/// the column must default to 'unspecified', the seeder must classify the standard units and seed the
/// nutrition-label factors, the one-time backfill must tag legacy rows by code, and the factor update
/// must touch ONLY rows still carrying the original seeded value.
///
/// The pivotal test is <see cref="Simplify_ReExpresses_Across_Real_Seeder_Output"/>: it runs
/// QuantityDisplay.Simplify against the seeder's <b>actual</b> output — the class of gap the vci8.2
/// golden tests could not catch, since those used synthetic exact-multiple units. With the label
/// factors (tsp 5, tbsp 15, cup 240) the within-family ratios are exactly 3 / 16, so 4 tbsp really does
/// simplify to ¼ cup against production data.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class UnitSystemTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "A UnitSystem-tagged unit round-trips through EF")]
    public async Task UnitSystem_RoundTrips_Through_EfMapping()
    {
        UnitId cupId;
        await using (var db1 = NewCatalogDb())
        {
            var cup = CatalogUnit.Create(_household, "cup", "cup", Dimension.Volume, 240m, unitSystem: UnitSystem.UsCustomary);
            var ml = CatalogUnit.Create(_household, "ml", "millilitre", Dimension.Volume, 1m, isBase: true, unitSystem: UnitSystem.Metric);
            var ea = CatalogUnit.Create(_household, "ea", "each", Dimension.Count, 1m, isBase: true);
            await db1.Units.AddRangeAsync(cup, ml, ea);
            await db1.SaveChangesAsync();
            cupId = cup.Id;
        }

        await using var db2 = NewCatalogDb();
        var loadedCup = await db2.Units.SingleAsync(u => u.Id == cupId);
        var loadedMl = await db2.Units.SingleAsync(u => u.Code == "ml");
        var loadedEa = await db2.Units.SingleAsync(u => u.Code == "ea");

        Assert.Equal(UnitSystem.UsCustomary, loadedCup.UnitSystem);
        Assert.Equal(UnitSystem.Metric, loadedMl.UnitSystem);
        Assert.Equal(UnitSystem.Unspecified, loadedEa.UnitSystem);
    }

    [Fact(DisplayName = "Migration defaults a column-less unit row to 'unspecified'")]
    public async Task Migration_Defaults_Existing_Rows_To_Unspecified()
    {
        // Simulate a pre-migration row: insert omitting unit_system entirely. The migration's column
        // default (defaultValue: "unspecified") is what an existing row would have received.
        await using var db1 = NewCatalogDb();
        await db1.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.units (id, household_id, symbol, name, dimension, factor_to_base, is_base, display_style)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, 'decimal')
            """,
            Guid.CreateVersion7(), _household.Value, "bunch", "bunch", "count", 1m, false);

        await using var db2 = NewCatalogDb();
        var loaded = await db2.Units.SingleAsync(u => u.Code == "bunch");
        Assert.Equal(UnitSystem.Unspecified, loaded.UnitSystem);
    }

    [Fact(DisplayName = "CHECK constraint rejects an unknown unit_system value")]
    public async Task CheckConstraint_Rejects_Unknown_UnitSystem_Value()
    {
        await using var db1 = NewCatalogDb();

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db1.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.units (id, household_id, symbol, name, dimension, factor_to_base, is_base, display_style, unit_system)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, 'decimal', {7})
            """,
            Guid.CreateVersion7(), _household.Value, "g", "gram", "mass", 1m, true, "imperial"));

        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact(DisplayName = "Seeder tags standard units by system and seeds nutrition-label volume factors")]
    public async Task Seeder_Assigns_Systems_And_Label_Factors()
    {
        await using (var seedDb = NewCatalogDb())
        {
            await new CatalogReferenceDataSeeder(seedDb).SeedAsync(_household);
        }

        await using var db2 = NewCatalogDb();
        var byCode = await db2.Units.ToDictionaryAsync(u => u.Code, u => u);

        // System classification.
        foreach (var code in new[] { "g", "kg", "mg", "ml", "l" })
            Assert.Equal(UnitSystem.Metric, byCode[code].UnitSystem);
        foreach (var code in new[] { "oz", "lb", "fl oz", "cup", "tsp", "tbsp" })
            Assert.Equal(UnitSystem.UsCustomary, byCode[code].UnitSystem);
        foreach (var code in new[] { "ea", "pk", "doz" })
            Assert.Equal(UnitSystem.Unspecified, byCode[code].UnitSystem);

        // Nutrition-label volume factors (cup already 240) — the exact 3 / 2 / 8 / 16 family ratios.
        Assert.Equal(5m, byCode["tsp"].FactorToBase);
        Assert.Equal(15m, byCode["tbsp"].FactorToBase);
        Assert.Equal(30m, byCode["fl oz"].FactorToBase);
        Assert.Equal(240m, byCode["cup"].FactorToBase);
    }

    [Fact(DisplayName = "Simplify re-expresses across the real seeder output (4 tbsp → ¼ cup, 3 tsp → 1 tbsp)")]
    public async Task Simplify_ReExpresses_Across_Real_Seeder_Output()
    {
        await using (var seedDb = NewCatalogDb())
        {
            await new CatalogReferenceDataSeeder(seedDb).SeedAsync(_household);
        }

        await using var db2 = NewCatalogDb();
        var units = await db2.Units.ToListAsync();
        var tbsp = units.Single(u => u.Code == "tbsp");
        var tsp = units.Single(u => u.Code == "tsp");
        var cup = units.Single(u => u.Code == "cup");
        var ml = units.Single(u => u.Code == "ml");

        // 4 tbsp → ¼ cup (4 × 15 / 240 = 0.25) — the gap the golden tests could not catch. Assert the
        // rendered target unit + display string, the real contract (raw decimals carry division noise).
        var (cupAmount, cupUnit) = QuantityDisplay.Simplify(4m, tbsp.Id.Value, units);
        Assert.Equal(cup.Id.Value, cupUnit);
        Assert.Equal("¼", QuantityDisplay.FormatAmount(cupAmount, cup.DisplayStyle));

        // 3 tsp → 1 tbsp (3 × 5 / 15; decimal division lands at 0.999…9 which snaps to "1").
        var (tbspAmount, tbspUnit) = QuantityDisplay.Simplify(3m, tsp.Id.Value, units);
        Assert.Equal(tbsp.Id.Value, tbspUnit);
        Assert.Equal("1", QuantityDisplay.FormatAmount(tbspAmount, tbsp.DisplayStyle));

        // Firewall against real data: authored ml (Metric) is never rewritten to cup (UsCustomary)
        // even though 480 / 240 = 2 is a whole ratio — the metric→imperial breach the tag closes.
        var (mlAmount, mlUnit) = QuantityDisplay.Simplify(480m, ml.Id.Value, units);
        Assert.Equal(ml.Id.Value, mlUnit);
        Assert.Equal(480m, mlAmount);
    }

    [Fact(DisplayName = "Backfill tags legacy units by code (case-insensitive), leaving user units Unspecified")]
    public async Task DataMigration_Backfills_Systems_By_Code()
    {
        // Insert 'unspecified' rows as a pre-feature household would have had them, then replay the
        // migration's one-time backfill. Uppercase 'CUP' / 'Ml' prove the case-insensitive match;
        // 'bunch' proves a user-created unit is left Unspecified.
        await using var db1 = NewCatalogDb();
        foreach (var (code, dimension, factor) in new[]
                 {
                     ("CUP", "volume", 240m),
                     ("Ml", "volume", 1m),
                     ("g", "mass", 1m),
                     ("oz", "mass", 28.3495m),
                     ("bunch", "count", 1m),
                 })
        {
            await db1.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO catalog.units (id, household_id, symbol, name, dimension, factor_to_base, is_base, display_style, unit_system)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, 'decimal', 'unspecified')
                """,
                Guid.CreateVersion7(), _household.Value, code, code, dimension, factor, false);
        }

        await db1.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.units SET unit_system = 'metric' WHERE lower(symbol) IN ('ml', 'l', 'g', 'kg', 'mg');");
        await db1.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.units SET unit_system = 'us_customary' WHERE lower(symbol) IN ('oz', 'lb', 'fl oz', 'cup', 'tsp', 'tbsp');");

        await using var db2 = NewCatalogDb();
        var byCode = await db2.Units.ToDictionaryAsync(u => u.Code, u => u.UnitSystem);

        Assert.Equal(UnitSystem.UsCustomary, byCode["CUP"]);
        Assert.Equal(UnitSystem.Metric, byCode["Ml"]);
        Assert.Equal(UnitSystem.Metric, byCode["g"]);
        Assert.Equal(UnitSystem.UsCustomary, byCode["oz"]);
        Assert.Equal(UnitSystem.Unspecified, byCode["bunch"]);
    }

    [Fact(DisplayName = "Factor update touches only rows still at the original seeded value")]
    public async Task DataMigration_FactorUpdate_Preserves_HandEdited_Factors()
    {
        // Two tsp rows: one still at the original seeded 4.92892 (should update to 5), one hand-edited
        // to 4.8 (must be left alone). Mirrors the migration's guarded UPDATE.
        await using var db1 = NewCatalogDb();
        var pristineId = Guid.CreateVersion7();
        var editedId = Guid.CreateVersion7();
        await db1.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.units (id, household_id, symbol, name, dimension, factor_to_base, is_base, display_style, unit_system)
            VALUES ({0}, {1}, 'tsp', 'teaspoon', 'volume', 4.92892, false, 'fraction', 'us_customary'),
                   ({2}, {1}, 'tbsp', 'tablespoon', 'volume', 4.8, false, 'fraction', 'us_customary')
            """,
            pristineId, _household.Value, editedId);

        await db1.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.units SET factor_to_base = 5 WHERE lower(symbol) = 'tsp' AND factor_to_base = 4.92892;");
        await db1.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.units SET factor_to_base = 15 WHERE lower(symbol) = 'tbsp' AND factor_to_base = 14.7868;");

        await using var db2 = NewCatalogDb();
        var pristine = await db2.Units.SingleAsync(u => u.Id == UnitId.From(pristineId));
        var edited = await db2.Units.SingleAsync(u => u.Id == UnitId.From(editedId));

        Assert.Equal(5m, pristine.FactorToBase);   // pristine tsp updated to label value
        Assert.Equal(4.8m, edited.FactorToBase);   // hand-edited tbsp (4.8 ≠ 14.7868) untouched
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
