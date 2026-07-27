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
/// Migration-behavior harness (plantry-2y1r) for
/// <c>Migrations/20260727061526_RemovePackAndDozenUnits.cs</c> — the first test in the suite that
/// seeds data ACROSS a migration boundary rather than only asserting a migration applies cleanly
/// against an empty database (<see cref="Infrastructure.MigrationTargetsConventionTests"/> /
/// <see cref="Infrastructure.MigratorPasswordReconciliationTests"/> both stop at "does it run", not
/// "is the data right afterwards"). Deferred from plantry-qszb's pass-1 review, restated in
/// plantry-n3r3's RECOMMEND text, and finally tracked/filed as its own bead by plantry-n3r3.
///
/// Deliberately does NOT use the shared <see cref="Infrastructure.PostgresFixture"/> /
/// <see cref="Infrastructure.PostgresCollection"/> — that fixture applies every migration for every
/// context up front, leaving no way to seed data BEFORE the migration under test runs. Each
/// [Fact] here boots its own disposable Postgres container, migrates Catalog only as far as
/// 20260724132700_AddServingUnit (the migration immediately preceding the one under test), seeds
/// pre-migration data via the real domain aggregates, then migrates forward through
/// RemovePackAndDozenUnits and asserts on the result — proving the two dedupe mechanisms the
/// migration's SQL comments describe rather than merely asserting the migration doesn't throw.
/// </summary>
public sealed class RemovePackAndDozenUnitsMigrationTests : IAsyncLifetime
{
    private const string BaselineMigration = "20260724132700_AddServingUnit";
    private const string MigrationUnderTest = "20260727061526_RemovePackAndDozenUnits";
    private const string MigrationsAssembly = "Plantry.Catalog.Infrastructure";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("plantry_migration_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact(DisplayName =
        "Guard path: a pk/doz-sourced conversion colliding with a PRE-EXISTING ea conversion is dropped, keeping the ea row's factor")]
    public async Task PreExistingEaConversion_Survives_PkAndDozSourcedCollisionsAreDropped()
    {
        var household = HouseholdId.New();
        var clock = SystemClock.Instance;

        await MigrateToAsync(BaselineMigration);

        UnitId eaId, gId;
        ProductId productId;
        await using (var seed = NewContext(household))
        {
            var ea = CatalogUnit.Create(household, "ea", "Each", Dimension.Count, 1m, isBase: true);
            var doz = CatalogUnit.Create(household, "doz", "Dozen", Dimension.Count, 12m);
            var pk = CatalogUnit.Create(household, "pk", "Pack", Dimension.Count, 1m);
            var g = CatalogUnit.Create(household, "g", "Gram", Dimension.Mass, 1m, isBase: true);
            await seed.Units.AddRangeAsync(ea, doz, pk, g);

            // One product with THREE conversions to 'g': the pre-existing ea->g, plus a doz->g and
            // a pk->g that will both relabel onto ea->g once the migration runs — each independently
            // colliding with the pre-existing ea->g row (the guard's target case).
            var product = Product.Create(household, "Eggs", ea.Id, clock);
            var eaConv = product.AddConversion(ea.Id, g.Id, 50m, clock);
            var dozConv = product.AddConversion(doz.Id, g.Id, 600m, clock);
            var pkConv = product.AddConversion(pk.Id, g.Id, 100m, clock);
            await seed.Products.AddAsync(product);

            await seed.SaveChangesAsync();

            // Pin explicit ids so the pre-existing ea->g row sorts HIGHEST. The post-relabel backstop
            // DELETE keeps the LOWEST id, so with these ids the backstop alone would keep a pk/doz-sourced
            // row — only the pre-relabel guard can leave factor 50 standing. Without this, ProductConversionId's
            // UUIDv7 generation leaves relative creation order to random sub-millisecond bits, and the guard
            // regressing to a no-op would only be caught by this test ~1 run in 3 (the backstop dedupe would
            // still collapse to a single row, uniformly at random among the three, ~1/3 of the time landing on
            // factor 50 by coincidence).
            await seed.Database.ExecuteSqlRawAsync(
                "UPDATE catalog.product_conversions SET id = {0} WHERE id = {1};",
                Guid.Parse("00000000-0000-7000-8000-000000000001"), dozConv.Id.Value);
            await seed.Database.ExecuteSqlRawAsync(
                "UPDATE catalog.product_conversions SET id = {0} WHERE id = {1};",
                Guid.Parse("00000000-0000-7000-8000-000000000002"), pkConv.Id.Value);
            await seed.Database.ExecuteSqlRawAsync(
                "UPDATE catalog.product_conversions SET id = {0} WHERE id = {1};",
                Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff"), eaConv.Id.Value);

            eaId = ea.Id;
            gId = g.Id;
            productId = product.Id;
        }

        await MigrateToAsync(MigrationUnderTest);

        await using var read = NewContext(household);
        var loaded = await read.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);

        var surviving = Assert.Single(loaded.Conversions);
        Assert.Equal(eaId, surviving.FromUnitId);
        Assert.Equal(gId, surviving.ToUnitId);
        Assert.Equal(50m, surviving.Factor);
    }

    [Fact(DisplayName =
        "Dedupe path: two pk/doz-sourced conversions colliding with EACH OTHER (no pre-existing ea row) collapse to exactly one surviving row")]
    public async Task TwoPkDozSourcedConversions_CollideWithEachOther_CollapseToOneRow()
    {
        var household = HouseholdId.New();
        var clock = SystemClock.Instance;

        await MigrateToAsync(BaselineMigration);

        UnitId eaId, gId;
        ProductId productId;
        await using (var seed = NewContext(household))
        {
            // The migration's relabel UPDATEs join doz_pk to ea_units ON household_id — an 'ea' unit
            // must exist for the household (as it always does: every household is seeded with an
            // 'ea' base unit) or the join yields zero rows and nothing relabels at all. Critically,
            // this household has NO ea->g conversion — only the units row exists, not a conversion
            // using it — so the pre-update guard (which only fires when a pre-existing non-pk/doz
            // conversion already occupies the post-relabel (from, to) pair) cannot catch anything.
            var ea = CatalogUnit.Create(household, "ea", "Each", Dimension.Count, 1m, isBase: true);
            var doz = CatalogUnit.Create(household, "doz", "Dozen", Dimension.Count, 12m);
            var pk = CatalogUnit.Create(household, "pk", "Pack", Dimension.Count, 1m);
            var g = CatalogUnit.Create(household, "g", "Gram", Dimension.Mass, 1m, isBase: true);
            await seed.Units.AddRangeAsync(ea, doz, pk, g);

            // Neither side is 'ea' before the relabel UPDATEs run, so the pre-update guard cannot
            // fire for either — both land on ea->g only once the UPDATEs complete, and it is the
            // post-relabel backstop DELETE that must collapse them. The product's DEFAULT unit is
            // 'pk' (not 'ea'), so this Fact also uniquely exercises the migration's FIRST statement
            // (the catalog.products.default_unit_id relabel) — asserted below alongside the surviving
            // conversion's identity, not just its count.
            var product = Product.Create(household, "Eggs", pk.Id, clock);
            product.AddConversion(pk.Id, g.Id, 100m, clock);
            product.AddConversion(doz.Id, g.Id, 600m, clock);
            await seed.Products.AddAsync(product);

            await seed.SaveChangesAsync();

            eaId = ea.Id;
            gId = g.Id;
            productId = product.Id;
        }

        await MigrateToAsync(MigrationUnderTest);

        await using var read = NewContext(household);
        var loaded = await read.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);

        // Which of the two colliding rows survives (factor 100 vs 600) is genuinely id-order
        // dependent — asserted is only what the migration GUARANTEES: exactly one row, relabeled
        // onto 'ea', and the product's own default_unit_id relabeled too (the migration's first
        // statement, otherwise unexercised by the guard-path Fact above).
        var surviving = Assert.Single(loaded.Conversions);
        Assert.Equal(eaId, surviving.FromUnitId);
        Assert.Equal(gId, surviving.ToUnitId);
        Assert.Equal(eaId, loaded.DefaultUnitId);
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
