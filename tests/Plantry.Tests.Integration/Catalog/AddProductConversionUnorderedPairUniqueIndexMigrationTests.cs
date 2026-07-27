using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Testcontainers.PostgreSql;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// Migration-behavior harness (plantry-pcfe, ADR-022 amendment) for
/// <c>Migrations/20260727194353_AddProductConversionUnorderedPairUniqueIndex.cs</c> — proves the
/// pre-index dedupe collapses each unordered-pair violation to its NEWEST (highest-id) row before
/// the unique expression index is created. This is the deliberate INVERSE of
/// <see cref="RemovePackAndDozenUnitsMigrationTests"/>'s backstop dedupe, which keeps the LOWEST
/// id — see that migration's own comment and ADR-022's 2026-07-27 amendment for why each migration
/// picks a different survivor for its own scenario. Follows the same per-Fact
/// disposable-container pattern as that harness: boot a fresh Postgres container, migrate to the
/// migration immediately preceding the one under test, seed pre-migration data, migrate through,
/// assert on the result.
///
/// Rows here are seeded via raw SQL rather than <see cref="Product.AddConversion"/> — this same
/// ticket also fixes <c>AddConversion</c> to enforce the unordered-pair invariant in memory, so a
/// fresh aggregate can no longer be coaxed into holding two colliding rows the way the pre-fix
/// code (and any data written before it shipped) could. Raw SQL is the only way to reproduce the
/// legacy violation this migration exists to clean up.
/// </summary>
public sealed class AddProductConversionUnorderedPairUniqueIndexMigrationTests : IAsyncLifetime
{
    private const string BaselineMigration = "20260727061526_RemovePackAndDozenUnits";
    private const string MigrationUnderTest = "20260727194353_AddProductConversionUnorderedPairUniqueIndex";
    private const string MigrationsAssembly = "Plantry.Catalog.Infrastructure";

    // Fixed, explicitly-ordered ids (mirrors RemovePackAndDozenUnitsMigrationTests' own pinned-id
    // technique) rather than relying on Guid.CreateVersion7()'s sub-millisecond timestamp
    // resolution to land in a particular order — deterministic regardless of how fast the test
    // machine executes the two inserts.
    private static readonly Guid OlderId = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid NewerId = Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff");
    // Deliberately LOWER than NewerId (and would be swept by an unscoped dedupe that ignores
    // product_id) but belongs to a completely different product — see
    // UnrelatedProductWithSamePair_Survives below.
    private static readonly Guid BystanderId = Guid.Parse("00000000-0000-7000-8000-000000000002");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("plantry_migration_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact(DisplayName = "Same-direction duplicate pair collapses to the NEWEST (highest-id) row")]
    public async Task SameDirectionDuplicatePair_CollapsesToNewestRow()
    {
        var household = HouseholdId.New();
        var clock = SystemClock.Instance;

        await MigrateToAsync(BaselineMigration);

        UnitId cupsId, gramsId;
        ProductId productId;
        await using (var seed = NewContext(household))
        {
            var cups = CatalogUnit.Create(household, "cup", "Cup", Dimension.Volume, 240m);
            var grams = CatalogUnit.Create(household, "g", "Gram", Dimension.Mass, 1m, isBase: true);
            await seed.Units.AddRangeAsync(cups, grams);

            var product = Product.Create(household, "Flour", grams.Id, clock);
            await seed.Products.AddAsync(product);
            await seed.SaveChangesAsync();

            productId = product.Id;
            cupsId = cups.Id;
            gramsId = grams.Id;

            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                OlderId, household.Value, productId.Value, cupsId.Value, gramsId.Value, 100m);
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                NewerId, household.Value, productId.Value, cupsId.Value, gramsId.Value, 120m);
        }

        await MigrateToAsync(MigrationUnderTest);

        await using var read = NewContext(household);
        var loaded = await read.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);

        var surviving = Assert.Single(loaded.Conversions);
        Assert.Equal(NewerId, surviving.Id.Value);
        Assert.Equal(120m, surviving.Factor);
    }

    [Fact(DisplayName = "Reverse-direction duplicate pair collapses to the NEWEST (highest-id) row")]
    public async Task ReverseDirectionDuplicatePair_CollapsesToNewestRow()
    {
        var household = HouseholdId.New();
        var clock = SystemClock.Instance;

        await MigrateToAsync(BaselineMigration);

        UnitId cupsId, gramsId;
        ProductId productId;
        await using (var seed = NewContext(household))
        {
            var cups = CatalogUnit.Create(household, "cup", "Cup", Dimension.Volume, 240m);
            var grams = CatalogUnit.Create(household, "g", "Gram", Dimension.Mass, 1m, isBase: true);
            await seed.Units.AddRangeAsync(cups, grams);

            var product = Product.Create(household, "Flour", grams.Id, clock);
            await seed.Products.AddAsync(product);
            await seed.SaveChangesAsync();

            productId = product.Id;
            cupsId = cups.Id;
            gramsId = grams.Id;

            // Same unordered pair {cup, g}, but opposite directions — this is the contradiction
            // rule 4's old directional lookup let through (the ADR-022 hole this ticket closes).
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                OlderId, household.Value, productId.Value, cupsId.Value, gramsId.Value, 100m);
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                NewerId, household.Value, productId.Value, gramsId.Value, cupsId.Value, 1m / 120m);
        }

        await MigrateToAsync(MigrationUnderTest);

        await using var read = NewContext(household);
        var loaded = await read.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);

        var surviving = Assert.Single(loaded.Conversions);
        Assert.Equal(NewerId, surviving.Id.Value);
        Assert.Equal(gramsId, surviving.FromUnitId);
        Assert.Equal(cupsId, surviving.ToUnitId);
    }

    [Fact(DisplayName = "An unrelated product's row for the SAME unordered pair is untouched by another product's dedupe")]
    public async Task UnrelatedProductWithSamePair_Survives()
    {
        // The dedupe DELETE (migration line ~39) scopes by `o.product_id = c.product_id` — this
        // Fact pins that scoping down. Without it, a global "keep the highest id per canonicalised
        // pair" dedupe would delete every OTHER product's row for the same {cup, g} pair that isn't
        // the single globally-highest id — exactly the defect shape RemovePackAndDozenUnits shipped
        // with an unscoped backstop.
        var household = HouseholdId.New();
        var clock = SystemClock.Instance;

        await MigrateToAsync(BaselineMigration);

        UnitId cupsId, gramsId;
        ProductId flourId, sugarId;
        await using (var seed = NewContext(household))
        {
            var cups = CatalogUnit.Create(household, "cup", "Cup", Dimension.Volume, 240m);
            var grams = CatalogUnit.Create(household, "g", "Gram", Dimension.Mass, 1m, isBase: true);
            await seed.Units.AddRangeAsync(cups, grams);

            var flour = Product.Create(household, "Flour", grams.Id, clock);
            var sugar = Product.Create(household, "Sugar", grams.Id, clock);
            await seed.Products.AddRangeAsync(flour, sugar);
            await seed.SaveChangesAsync();

            flourId = flour.Id;
            sugarId = sugar.Id;
            cupsId = cups.Id;
            gramsId = grams.Id;

            // Flour: a genuine same-pair duplicate that must collapse to NewerId (mirrors
            // SameDirectionDuplicatePair_CollapsesToNewestRow).
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                OlderId, household.Value, flourId.Value, cupsId.Value, gramsId.Value, 100m);
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                NewerId, household.Value, flourId.Value, cupsId.Value, gramsId.Value, 120m);

            // Sugar: a bystander with a SINGLE row for the exact same unordered pair {cup, g}, whose
            // id (BystanderId) is lower than Flour's surviving NewerId. An unscoped dedupe (missing
            // the product_id predicate) would treat all three rows as one group and delete this one
            // too, since it is not the group's globally-highest id.
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                BystanderId, household.Value, sugarId.Value, cupsId.Value, gramsId.Value, 200m);
        }

        await MigrateToAsync(MigrationUnderTest);

        await using var read = NewContext(household);
        var loadedFlour = await read.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == flourId);
        var loadedSugar = await read.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == sugarId);

        var flourSurviving = Assert.Single(loadedFlour.Conversions);
        Assert.Equal(NewerId, flourSurviving.Id.Value);

        var sugarSurviving = Assert.Single(loadedSugar.Conversions);
        Assert.Equal(BystanderId, sugarSurviving.Id.Value);
        Assert.Equal(200m, sugarSurviving.Factor);
    }

    [Fact(DisplayName = "Three distinct unordered pairs on the same product all survive — the dedupe never conflates different pairs")]
    public async Task DistinctUnorderedPairsOnSameProduct_AllSurvive()
    {
        // The single most important safety property of a destructive dedupe: it must not delete
        // legitimate, non-duplicate data. LEAST/GREATEST canonicalise a PAIR, not a single unit —
        // drop either one and the dedupe degenerates to grouping by just the low (or just the high)
        // unit id, which would wrongly conflate two DIFFERENT pairs that happen to share one unit.
        // A→B and A→C both have LEAST = A if A sorts lowest, so dropping GREATEST would conflate
        // them; A→C and B→C both have GREATEST = C if C sorts highest, so dropping LEAST would
        // conflate THOSE. Seeding all three edges of a triangle {A,B}, {A,C}, {B,C} on one product
        // exercises both failure modes in a single Fact: all three rows must survive.
        var household = HouseholdId.New();
        var clock = SystemClock.Instance;

        await MigrateToAsync(BaselineMigration);

        ProductId productId;
        UnitId aId, bId, cId;
        await using (var seed = NewContext(household))
        {
            var u1 = CatalogUnit.Create(household, "u1", "Unit 1", Dimension.Count, 1m, isBase: true);
            var u2 = CatalogUnit.Create(household, "u2", "Unit 2", Dimension.Count, 1m);
            var u3 = CatalogUnit.Create(household, "u3", "Unit 3", Dimension.Count, 1m);
            await seed.Units.AddRangeAsync(u1, u2, u3);
            await seed.SaveChangesAsync();

            // Order by Postgres uuid btree ordering, NOT Guid.CompareTo (which uses .NET's
            // field-based ordering and does not match). Guid's canonical "D"-format hex string is
            // byte-for-byte the RFC-4122 big-endian layout Postgres uuid_cmp memcmps and Npgsql
            // writes to the wire, so ordinal string comparison of that string reproduces Postgres's
            // own ordering exactly.
            var ordered = new[] { u1, u2, u3 }
                .OrderBy(u => u.Id.Value.ToString("D"), StringComparer.Ordinal)
                .ToList();
            var a = ordered[0];
            var b = ordered[1];
            var c = ordered[2];

            var product = Product.Create(household, "Triangle", a.Id, clock);
            await seed.Products.AddAsync(product);
            await seed.SaveChangesAsync();

            productId = product.Id;
            aId = a.Id;
            bId = b.Id;
            cId = c.Id;

            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                Guid.CreateVersion7(), household.Value, productId.Value, aId.Value, bId.Value, 2m);
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                Guid.CreateVersion7(), household.Value, productId.Value, aId.Value, cId.Value, 3m);
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
                Guid.CreateVersion7(), household.Value, productId.Value, bId.Value, cId.Value, 5m);
        }

        await MigrateToAsync(MigrationUnderTest);

        await using var read = NewContext(household);
        var loaded = await read.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);

        Assert.Equal(3, loaded.Conversions.Count);
        var pairs = loaded.Conversions.Select(x => (x.FromUnitId, x.ToUnitId)).ToHashSet();
        Assert.Contains((aId, bId), pairs);
        Assert.Contains((aId, cId), pairs);
        Assert.Contains((bId, cId), pairs);
    }

    private async Task MigrateToAsync(string targetMigration)
    {
        await using var ctx = NewContext(HouseholdId.New());
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private CatalogDbContext NewContext(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly(MigrationsAssembly))
            .Options;
        var ctx = new CatalogDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
