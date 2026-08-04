using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Infrastructure;
using Plantry.Planning.Infrastructure;
using Plantry.Migrator;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Testcontainers.PostgreSql;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// Proves the product-dish snapshot migration against a real pre-migration schema.  The shared
/// integration fixture has already applied every migration by the time a test runs, so this test
/// owns a disposable database, migrates Identity + Catalog first, migrates MealPlanning only to the
/// old schema, seeds legacy rows, and then advances through the migration under test.
/// </summary>
public sealed class ProductDishQuantitySnapshotMigrationTests : IAsyncLifetime
{
    private const string BaselineMigration = "20260711110223_AddMealSlotIncludeInAutoPlan";
    private const string MigrationUnderTest = "20260801090000_ProductDishQuantitySnapshot";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("plantry_meal_dish_migration_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact(DisplayName = "Product dishes backfill quantity/unit snapshots while recipe rows keep servings")]
    public async Task Backfill_PreservesProductMeaningAndRecipeShape()
    {
        var householdId = HouseholdId.New();
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

        await MigrateIdentityAndCatalogAsync();
        var (productId, defaultUnitId) = await SeedCatalogProductAsync(householdId, clock);
        await MigrateMealPlanningToAsync(BaselineMigration);
        await SeedLegacyMealPlanRowsAsync(householdId, productId.Value);

        await MigrateMealPlanningToAsync(MigrationUnderTest);

        await using var read = NewMealPlanningContext();
        var dishes = await read.PlannedDishes
            .IgnoreQueryFilters()
            .OrderBy(d => d.Ordinal)
            .ToListAsync();

        Assert.Equal(2, dishes.Count);
        var productDish = Assert.Single(dishes, d => d.ProductId == productId.Value);
        Assert.Equal(2m, productDish.Quantity);
        Assert.Equal(defaultUnitId.Value, productDish.UnitId);
        Assert.Null(productDish.Servings);

        var recipeDish = Assert.Single(dishes, d => d.RecipeId == Guid.Parse("00000000-0000-7000-8000-000000000002"));
        Assert.Equal(3, recipeDish.Servings);
        Assert.Null(recipeDish.Quantity);
        Assert.Null(recipeDish.UnitId);
    }

    private async Task MigrateIdentityAndCatalogAsync()
    {
        foreach (var target in MigrationTargets.All.Take(2))
        {
            await using var db = target.CreateContext(_container.GetConnectionString());
            await db.Database.MigrateAsync();
        }
    }

    private async Task MigrateMealPlanningToAsync(string migration)
    {
        await using var db = NewMealPlanningContext();
        await db.Database.MigrateAsync(migration);
    }

    private async Task<(ProductId ProductId, UnitId DefaultUnitId)> SeedCatalogProductAsync(
        HouseholdId householdId,
        IClock clock)
    {
        await using var db = NewCatalogContext();
        var unit = Unit.Create(householdId, "kg", "Kilogram", Dimension.Mass, 1m, isBase: true);
        var product = Product.Create(householdId, "Historical chicken", unit.Id, clock);
        product.Archive(clock); // archived products are historical and must still backfill
        await db.Units.AddAsync(unit);
        await db.Products.AddAsync(product);
        await db.SaveChangesAsync();
        return (product.Id, unit.Id);
    }

    private async Task SeedLegacyMealPlanRowsAsync(HouseholdId householdId, Guid productId)
    {
        await using var db = NewMealPlanningContext();
        var planId = Guid.Parse("00000000-0000-7000-8000-000000000010");
        var slotConfigId = Guid.Parse("00000000-0000-7000-8000-000000000011");
        var slotId = Guid.Parse("00000000-0000-7000-8000-000000000012");
        var mealId = Guid.Parse("00000000-0000-7000-8000-000000000013");
        var productDishId = Guid.Parse("00000000-0000-7000-8000-000000000014");
        var recipeDishId = Guid.Parse("00000000-0000-7000-8000-000000000015");
        var userId = Guid.Parse("00000000-0000-7000-8000-000000000016");
        var weekStart = new DateOnly(2026, 8, 3);
        var createdAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO meal_planning.meal_plan
                (meal_plan_id, household_id, week_start, created_at, updated_at)
            VALUES ({0}, {1}, {2}, {3}, {3});
            INSERT INTO meal_planning.meal_slot_config
                (meal_slot_config_id, household_id, created_at, updated_at)
            VALUES ({4}, {1}, {3}, {3});
            INSERT INTO meal_planning.meal_slot
                (meal_slot_id, household_id, meal_slot_config_id, label, ordinal, default_attendees, archived_at)
            VALUES ({5}, {1}, {4}, 'Dinner', 1, ARRAY[]::uuid[], NULL);
            INSERT INTO meal_planning.planned_meal
                (planned_meal_id, household_id, meal_plan_id, date, meal_slot_id, attendees_override,
                 reasoning, note, source, created_by, updated_by, created_at, updated_at, ordinal)
            VALUES ({6}, {1}, {0}, {2}, {5}, NULL, NULL, NULL, 'manual', {7}, {7}, {3}, {3}, 1);
            INSERT INTO meal_planning.planned_dish
                (planned_dish_id, household_id, planned_meal_id, recipe_id, product_id, servings, ordinal)
            VALUES ({8}, {1}, {6}, NULL, {9}, 2, 1),
                   ({10}, {1}, {6}, {11}, NULL, 3, 2);
            """,
            planId,
            householdId.Value,
            weekStart,
            createdAt,
            slotConfigId,
            slotId,
            mealId,
            userId,
            productDishId,
            productId,
            recipeDishId,
            Guid.Parse("00000000-0000-7000-8000-000000000002"));
    }

    private CatalogDbContext NewCatalogContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_container.GetConnectionString(),
                npgsql => npgsql.MigrationsAssembly("Plantry.Catalog.Infrastructure"))
            .Options;
        return new CatalogDbContext(options);
    }

    private MealPlanningDbContext NewMealPlanningContext()
    {
        var options = new DbContextOptionsBuilder<MealPlanningDbContext>()
            .UseNpgsql(_container.GetConnectionString(),
                npgsql => npgsql.MigrationsAssembly("Plantry.Planning.Infrastructure"))
            .Options;
        return new MealPlanningDbContext(options);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
