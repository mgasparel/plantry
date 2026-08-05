using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Plantry.Composition.Infrastructure;
using Plantry.Identity.Infrastructure;
using Plantry.Intake.Infrastructure;
using Plantry.Market.Infrastructure;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.Planning.Infrastructure;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Testcontainers.PostgreSql;
using Xunit;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Integration.Housekeeping;

/// <summary>
/// Migration-behavior harness (plantry-sn6v, mirroring plantry-2y1r's
/// <see cref="Plantry.Tests.Integration.Catalog.RemovePackAndDozenUnitsMigrationTests"/>) for
/// <c>Migrations/20260805211458_BackfillYieldProductsIsProduced.cs</c> — proves the data backfill
/// flags the right rows and only the right rows, not merely that the SQL executes without error.
///
/// Deliberately does NOT use the shared <see cref="Infrastructure.PostgresFixture"/> — that fixture
/// migrates every context to latest up front, leaving no seam to seed pre-migration data. Instead
/// this boots its own disposable Postgres container, migrates every non-Housekeeping context in
/// <see cref="Plantry.Migrator.MigrationTargets"/> order to latest (so <c>catalog.products.is_produced</c>
/// and <c>recipes.recipe.yield_product_id</c> both already exist), migrates HousekeepingDbContext only
/// as far as the migration immediately preceding the one under test, seeds pre-migration data via the
/// real domain aggregates, then migrates forward through the backfill and asserts on the result.
/// </summary>
public sealed class BackfillYieldProductsIsProducedMigrationTests : IAsyncLifetime
{
    private const string HousekeepingBaselineMigration = "20260727062625_DeletePackAndDozenUnits";
    private const string HousekeepingMigrationUnderTest = "20260805211458_BackfillYieldProductsIsProduced";
    private const string HousekeepingMigrationsAssembly = "Plantry.Composition.Infrastructure";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("plantry_migration_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact(DisplayName =
        "Both an auto-created yield product and an author-chosen existing yield product are flagged is_produced=true; an unrelated ordinary product is left false")]
    public async Task YieldProducts_AreFlagged_OrdinaryProductIsNot()
    {
        var household = HouseholdId.New();
        var clock = SystemClock.Instance;

        await MigrateNonHousekeepingContextsToLatestAsync();
        await MigrateHousekeepingToAsync(HousekeepingBaselineMigration);

        ProductId autoYieldId, chosenYieldId, ordinaryId;
        await using (var catalog = NewCatalogContext(household))
        {
            var unit = CatalogUnit.Create(household, "ea", "Each", Dimension.Count, 1m, isBase: true);
            await catalog.Units.AddAsync(unit);

            var autoYield = Product.Create(household, "Roast Chicken (leftovers)", unit.Id, clock);
            var chosenYield = Product.Create(household, "Chicken Stock", unit.Id, clock);
            var ordinary = Product.Create(household, "Milk", unit.Id, clock);
            await catalog.Products.AddRangeAsync(autoYield, chosenYield, ordinary);

            await catalog.SaveChangesAsync();

            autoYieldId = autoYield.Id;
            chosenYieldId = chosenYield.Id;
            ordinaryId = ordinary.Id;
        }

        await using (var recipes = NewRecipesContext(household))
        {
            var withAutoYield = Recipe.Create(household, "Roast Chicken", 4, clock).Value;
            withAutoYield.SetYield(autoYieldId.Value, 2m, Guid.NewGuid(), clock);

            var withChosenYield = Recipe.Create(household, "Chicken Soup", 4, clock).Value;
            withChosenYield.SetYield(chosenYieldId.Value, 1m, Guid.NewGuid(), clock);

            await recipes.Recipes.AddRangeAsync(withAutoYield, withChosenYield);
            await recipes.SaveChangesAsync();
        }

        await MigrateHousekeepingToAsync(HousekeepingMigrationUnderTest);

        await using var read = NewCatalogContext(household);
        var autoYieldProduct = await read.Products.SingleAsync(p => p.Id == autoYieldId);
        var chosenYieldProduct = await read.Products.SingleAsync(p => p.Id == chosenYieldId);
        var ordinaryProduct = await read.Products.SingleAsync(p => p.Id == ordinaryId);

        Assert.True(autoYieldProduct.IsProduced);
        // Pins the deliberate over-flag documented in the migration comment: the backfill cannot
        // distinguish an auto-created yield product from one the author explicitly picked as an
        // existing yield target, so BOTH are flagged. A future narrowing that changes this is a
        // conscious change to this assertion, not a silent regression.
        Assert.True(chosenYieldProduct.IsProduced);
        Assert.False(ordinaryProduct.IsProduced);
    }

    [Fact(DisplayName =
        "A recipe with no yield declared (yield_product_id IS NULL) flags no product")]
    public async Task RecipeWithNoYield_FlagsNoProduct()
    {
        var household = HouseholdId.New();
        var clock = SystemClock.Instance;

        await MigrateNonHousekeepingContextsToLatestAsync();
        await MigrateHousekeepingToAsync(HousekeepingBaselineMigration);

        ProductId ordinaryId;
        await using (var catalog = NewCatalogContext(household))
        {
            var unit = CatalogUnit.Create(household, "ea", "Each", Dimension.Count, 1m, isBase: true);
            await catalog.Units.AddAsync(unit);

            var ordinary = Product.Create(household, "Milk", unit.Id, clock);
            await catalog.Products.AddAsync(ordinary);

            await catalog.SaveChangesAsync();
            ordinaryId = ordinary.Id;
        }

        await using (var recipes = NewRecipesContext(household))
        {
            var noYield = Recipe.Create(household, "Plain Toast", 2, clock).Value;
            await recipes.Recipes.AddAsync(noYield);
            await recipes.SaveChangesAsync();
        }

        await MigrateHousekeepingToAsync(HousekeepingMigrationUnderTest);

        await using var read = NewCatalogContext(household);
        var ordinaryProduct = await read.Products.SingleAsync(p => p.Id == ordinaryId);
        Assert.False(ordinaryProduct.IsProduced);
    }

    /// <summary>
    /// Migrates every context OTHER than Housekeeping to latest — mirrors
    /// <see cref="Plantry.Migrator.MigrationTargets.All"/>'s order up to (but not including)
    /// HousekeepingDbContext, which this harness deliberately stops short of so it can seed data
    /// before the migration under test.
    /// </summary>
    private async Task MigrateNonHousekeepingContextsToLatestAsync()
    {
        await MigrateAsync(new DbContextOptionsBuilder<PlantryIdentityDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Identity.Infrastructure")).Options,
            o => new PlantryIdentityDbContext(o));

        await MigrateAsync(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Pantry.Infrastructure")).Options,
            o => new CatalogDbContext(o));

        await MigrateAsync(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Pantry.Infrastructure")).Options,
            o => new InventoryDbContext(o));

        await MigrateAsync(new DbContextOptionsBuilder<MarketDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Market.Infrastructure")).Options,
            o => new MarketDbContext(o));

        await MigrateAsync(new DbContextOptionsBuilder<IntakeDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Intake.Infrastructure")).Options,
            o => new IntakeDbContext(o));

        await MigrateAsync(new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Recipes.Infrastructure")).Options,
            o => new RecipesDbContext(o));

        await MigrateAsync(new DbContextOptionsBuilder<ShoppingDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Planning.Infrastructure")).Options,
            o => new ShoppingDbContext(o));

        await MigrateAsync(new DbContextOptionsBuilder<MealPlanningDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Planning.Infrastructure")).Options,
            o => new MealPlanningDbContext(o));
    }

    private static async Task MigrateAsync<TContext>(DbContextOptions<TContext> options, Func<DbContextOptions<TContext>, TContext> factory)
        where TContext : DbContext
    {
        await using var ctx = factory(options);
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync();
    }

    private async Task MigrateHousekeepingToAsync(string targetMigration)
    {
        await using var ctx = NewHousekeepingContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private HousekeepingDbContext NewHousekeepingContext()
    {
        var opts = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly(HousekeepingMigrationsAssembly))
            .Options;
        return new HousekeepingDbContext(opts);
    }

    private CatalogDbContext NewCatalogContext(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Pantry.Infrastructure"))
            .Options;
        var ctx = new CatalogDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private RecipesDbContext NewRecipesContext(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("Plantry.Recipes.Infrastructure"))
            .Options;
        var ctx = new RecipesDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
