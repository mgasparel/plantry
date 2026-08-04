using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Infrastructure;
using Plantry.Market.Infrastructure;
using Plantry.Identity.Infrastructure;
using Plantry.Intake.Infrastructure;
using Plantry.Inventory.Infrastructure;
using Plantry.Planning.Infrastructure;
using Plantry.Recipes.Infrastructure;
using Plantry.Composition.Infrastructure;

namespace Plantry.Migrator;

/// <summary>
/// One migration-owning DbContext: the EF Core migrations assembly name, the Postgres schema(s)
/// it manages, a display name for console logging, and a factory that builds the DbContext
/// from an owner connection string. Most contexts own exactly one schema; <see cref="MarketDbContext"/>
/// owns two (<c>pricing</c> and <c>deals</c>, unified into one DbContext by plantry-g3da.7 without
/// moving the underlying data — ADR-024 §"Physical schemas do not move on day one") so
/// <see cref="Schemas"/> is a list rather than a single string.
/// </summary>
public sealed record MigrationTarget(
    string MigrationsAssembly,
    IReadOnlyList<string> Schemas,
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
/// Plantry.Market.Infrastructure owns ONE entry — MarketDbContext, spanning both the <c>pricing</c>
/// and <c>deals</c> schemas — since plantry-g3da.7 unified the interim PricingDbContext/DealsDbContext
/// split (ADR-024) into a single DbContext with a single migration history (hosted in the
/// <c>pricing</c> schema, MarketDbContext's EF default schema). See
/// <c>Plantry.Market.Infrastructure/Migrations/Market/InitialMarketSchema</c> for the squashed baseline
/// and <c>docs/Operations/deployment.md</c> for the one-time deploy reconciliation an already-deployed
/// database needs to adopt it.
///
/// ORDER IS LOAD-BEARING. Plantry.Identity.Infrastructure MUST remain first — its initial
/// migration creates the <c>app_user</c> role that every other schema's RLS policies (and
/// the app_user-authenticated test/runtime connections) depend on. HousekeepingDbContext's migrations
/// MUST remain LAST — its 20260727062625_DeletePackAndDozenUnits data migration deletes
/// catalog.units rows only after every other context's RelabelPackAndDozenUnitReferences migration
/// has run (plantry-qszb); a context appended after it, or a reorder, would delete units still
/// referenced by an un-relabeled schema. (plantry-g3da.2, ADR-024 Phase A: HousekeepingDbContext and
/// its migrations physically moved from the retired Plantry.Housekeeping.Infrastructure project into
/// Plantry.Web/Housekeeping/Persistence; plantry-g3da.9, ADR-024 ratified option B, moved them again —
/// this time into Plantry.Composition.Infrastructure, the read layer's standing persistence home, so
/// the MigrationsAssembly below is "Plantry.Composition.Infrastructure" and Plantry.Migrator no longer
/// references Plantry.Web at all. The schema/table are byte-identical across both moves, only the
/// owning assembly changed.)
/// </summary>
public static class MigrationTargets
{
    public static readonly IReadOnlyList<MigrationTarget> All =
    [
        Target<PlantryIdentityDbContext>("Plantry.Identity.Infrastructure", "identity"),
        Target<CatalogDbContext>("Plantry.Catalog.Infrastructure", "catalog"),
        Target<InventoryDbContext>("Plantry.Inventory.Infrastructure", "inventory"),
        Target<MarketDbContext>("Plantry.Market.Infrastructure", "pricing", "deals"),
        Target<IntakeDbContext>("Plantry.Intake.Infrastructure", "intake"),
        Target<RecipesDbContext>("Plantry.Recipes.Infrastructure", "recipes"),
        Target<ShoppingDbContext>("Plantry.Planning.Infrastructure", "shopping"),
        Target<MealPlanningDbContext>("Plantry.Planning.Infrastructure", "meal_planning"),
        Target<HousekeepingDbContext>("Plantry.Composition.Infrastructure", "housekeeping"),
    ];

    private static MigrationTarget Target<TContext>(string migrationsAssembly, params string[] schemas)
        where TContext : DbContext
    {
        DbContext CreateContext(string connStr)
        {
            var opts = new DbContextOptionsBuilder<TContext>()
                .UseNpgsql(connStr, npgsql => npgsql.MigrationsAssembly(migrationsAssembly))
                .Options;
            return (TContext)Activator.CreateInstance(typeof(TContext), opts)!;
        }

        return new MigrationTarget(migrationsAssembly, schemas, typeof(TContext).Name, CreateContext);
    }
}
