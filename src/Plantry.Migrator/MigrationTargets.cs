using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Infrastructure;
using Plantry.Deals.Infrastructure;
using Plantry.Housekeeping.Infrastructure;
using Plantry.Identity.Infrastructure;
using Plantry.Intake.Infrastructure;
using Plantry.Inventory.Infrastructure;
using Plantry.MealPlanning.Infrastructure;
using Plantry.Pricing.Infrastructure;
using Plantry.Recipes.Infrastructure;
using Plantry.Shopping.Infrastructure;

namespace Plantry.Migrator;

/// <summary>
/// One migration-owning DbContext: the EF Core migrations assembly name, the Postgres schema
/// it manages, a display name for console logging, and a factory that builds the DbContext
/// from an owner connection string.
/// </summary>
public sealed record MigrationTarget(
    string MigrationsAssembly,
    string Schema,
    string DisplayName,
    Func<string, DbContext> CreateContext);

/// <summary>
/// The single, ordered source of truth for every migration-owning DbContext in Plantry.
/// Plantry.Migrator's Program.cs and the test-only PostgresFixture both iterate this list
/// instead of each maintaining its own hand-written per-context migration bootstrap.
///
/// This registry exists because those two lists (plus a third — Respawn's
/// SchemasToInclude) used to be hand-copied in three places and silently drifted: the
/// Migrator's copy omitted Plantry.Housekeeping.Infrastructure entirely, so production
/// deploys never created the housekeeping schema even though the full test suite passed
/// (plantry-eimm). A filesystem convention test asserts every <c>*.Infrastructure</c>
/// project with a <c>Migrations/</c> folder has an entry here, so a future bounded context
/// can no longer go missing the same way.
///
/// ORDER IS LOAD-BEARING. Plantry.Identity.Infrastructure MUST remain first — its initial
/// migration creates the <c>app_user</c> role that every other schema's RLS policies (and
/// the app_user-authenticated test/runtime connections) depend on.
/// </summary>
public static class MigrationTargets
{
    public static readonly IReadOnlyList<MigrationTarget> All =
    [
        Target<PlantryIdentityDbContext>("Plantry.Identity.Infrastructure", "identity"),
        Target<CatalogDbContext>("Plantry.Catalog.Infrastructure", "catalog"),
        Target<InventoryDbContext>("Plantry.Inventory.Infrastructure", "inventory"),
        Target<PricingDbContext>("Plantry.Pricing.Infrastructure", "pricing"),
        Target<IntakeDbContext>("Plantry.Intake.Infrastructure", "intake"),
        Target<RecipesDbContext>("Plantry.Recipes.Infrastructure", "recipes"),
        Target<ShoppingDbContext>("Plantry.Shopping.Infrastructure", "shopping"),
        Target<MealPlanningDbContext>("Plantry.MealPlanning.Infrastructure", "meal_planning"),
        Target<DealsDbContext>("Plantry.Deals.Infrastructure", "deals"),
        Target<HousekeepingDbContext>("Plantry.Housekeeping.Infrastructure", "housekeeping"),
    ];

    private static MigrationTarget Target<TContext>(string migrationsAssembly, string schema)
        where TContext : DbContext
    {
        DbContext CreateContext(string connStr)
        {
            var opts = new DbContextOptionsBuilder<TContext>()
                .UseNpgsql(connStr, npgsql => npgsql.MigrationsAssembly(migrationsAssembly))
                .Options;
            return (TContext)Activator.CreateInstance(typeof(TContext), opts)!;
        }

        return new MigrationTarget(migrationsAssembly, schema, typeof(TContext).Name, CreateContext);
    }
}
