using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Infrastructure;
using Plantry.Market.Infrastructure;
using Plantry.Identity.Infrastructure;
using Plantry.Intake.Infrastructure;
using Plantry.Planning.Infrastructure;
using Plantry.Recipes.Infrastructure;
using Plantry.Composition.Infrastructure;

namespace Plantry.Migrator;

/// <summary>
/// One migration-owning DbContext: the EF Core migrations assembly name, the Postgres schema(s)
/// it manages, a display name for console logging, and a factory that builds the DbContext
/// from an owner connection string. Most contexts own exactly one schema; <see cref="MarketDbContext"/>
/// owns two (<c>pricing</c> and <c>deals</c>, unified into one DbContext by plantry-g3da.7 without
/// moving the underlying data — ADR-024 §"Physical schemas do not move on day one") and
/// <see cref="PantryDbContext"/> owns two (<c>catalog</c> and <c>inventory</c>, unified by
/// plantry-g3da.10 for the same reason), so <see cref="Schemas"/> is a list rather than a single
/// string.
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
/// Plantry.Pantry.Infrastructure likewise owns ONE entry — PantryDbContext, spanning both the
/// <c>catalog</c> and <c>inventory</c> schemas — since plantry-g3da.10 unified the interim
/// CatalogDbContext/InventoryDbContext split (plantry-g3da.6, ADR-024) into a single DbContext with
/// a single migration history (hosted in the <c>catalog</c> schema, PantryDbContext's EF default
/// schema). See <c>Plantry.Pantry.Infrastructure/Migrations/Pantry/InitialPantrySchema</c> for the
/// squashed baseline and <c>docs/Operations/deployment.md</c> for the one-time deploy reconciliation
/// an already-deployed database needs to adopt it.
///
/// Plantry.Planning.Infrastructure likewise owns ONE entry — PlanningDbContext, spanning both the
/// <c>shopping</c> and <c>meal_planning</c> schemas — since plantry-g3da.8 unified the interim
/// ShoppingDbContext/MealPlanningDbContext split (plantry-g3da.5, ADR-024) into a single DbContext with
/// a single migration history (hosted in the <c>shopping</c> schema, PlanningDbContext's EF default
/// schema). See <c>Plantry.Planning.Infrastructure/Migrations/Planning/InitialPlanningSchema</c> for
/// the squashed baseline and <c>docs/Operations/deployment.md</c> for the one-time deploy
/// reconciliation an already-deployed database needs to adopt it.
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
        Target<PantryDbContext>("Plantry.Pantry.Infrastructure", "catalog", "inventory"),
        Target<MarketDbContext>("Plantry.Market.Infrastructure", "pricing", "deals"),
        Target<IntakeDbContext>("Plantry.Intake.Infrastructure", "intake"),
        Target<RecipesDbContext>("Plantry.Recipes.Infrastructure", "recipes"),
        Target<PlanningDbContext>("Plantry.Planning.Infrastructure", "shopping", "meal_planning"),
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
