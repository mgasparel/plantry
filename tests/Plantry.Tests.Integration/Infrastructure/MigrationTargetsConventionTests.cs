using Plantry.Migrator;
using Xunit;

namespace Plantry.Tests.Integration.Infrastructure;

/// <summary>
/// Convention guard for plantry-eimm: every <c>*.Infrastructure</c> project under <c>src/</c> that
/// owns a <c>Migrations/</c> folder MUST be registered in <see cref="MigrationTargets.All"/> — the
/// single shared list that Plantry.Migrator's Program.cs and PostgresFixture both iterate. This is a
/// plain filesystem scan against the real registry (no text-scraping of Program.cs source), so a
/// bounded context that ships an EF migration but forgets to add itself to the registry fails this
/// test instead of silently missing its schema in production (the original bug: Housekeeping had
/// migrations but was never registered in the Migrator, so `housekeeping.dismissal` was never
/// created outside the test suite's own parallel bootstrap).
/// </summary>
public sealed class MigrationTargetsConventionTests
{
    [Fact(DisplayName = "Every *.Infrastructure project with a Migrations folder is registered in MigrationTargets")]
    public void EveryMigrationOwningInfrastructureProject_IsRegisteredInMigrationTargets()
    {
        var srcRoot = Path.Combine(RepoRoot(), "src");

        var migrationOwningProjects = Directory.EnumerateDirectories(srcRoot, "*.Infrastructure", SearchOption.TopDirectoryOnly)
            .Where(dir => Directory.Exists(Path.Combine(dir, "Migrations")))
            .Select(dir => Path.GetFileName(dir)!)
            .ToList();

        // plantry-g3da.2 (ADR-024 Phase A): HousekeepingDbContext's migrations relocated from the retired
        // Plantry.Housekeeping.Infrastructure project into Plantry.Web/Housekeeping/Persistence/Migrations
        // when the Housekeeping bounded context was dissolved. Plantry.Web is not a *.Infrastructure
        // project, so the glob above can no longer see them — widen the scan: if Plantry.Web owns any
        // Migrations/ folder anywhere under it (recursively, since it also hosts ordinary app code
        // alongside Housekeeping/Persistence/Migrations), require "Plantry.Web" to be registered too,
        // closing the same blind spot the glob covers for *.Infrastructure projects. This keeps the
        // convention intact instead of silently losing coverage the moment a *.Infrastructure project's
        // migrations move to the composition root.
        var webRoot = Path.Combine(srcRoot, "Plantry.Web");
        if (Directory.Exists(webRoot) &&
            Directory.EnumerateDirectories(webRoot, "Migrations", SearchOption.AllDirectories).Any())
        {
            migrationOwningProjects.Add("Plantry.Web");
        }

        migrationOwningProjects = migrationOwningProjects.OrderBy(name => name, StringComparer.Ordinal).ToList();

        // Sanity check on the scan itself: if this drops to zero (or an implausibly low number)
        // the filesystem probe is broken and the test would pass vacuously — fail loudly instead.
        Assert.True(
            migrationOwningProjects.Count >= 5,
            $"Expected several *.Infrastructure projects with a Migrations/ folder under {srcRoot}, " +
            $"found {migrationOwningProjects.Count}. The filesystem scan is likely broken.");

        var registeredAssemblies = MigrationTargets.All
            .Select(t => t.MigrationsAssembly)
            .ToHashSet(StringComparer.Ordinal);

        var missing = migrationOwningProjects
            .Where(name => !registeredAssemblies.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The following *.Infrastructure projects have a Migrations/ folder but no entry in " +
            "Plantry.Migrator.MigrationTargets.All: " + string.Join(", ", missing));
    }

    [Fact(DisplayName = "Identity is first and Housekeeping is last in MigrationTargets.All (ORDER IS LOAD-BEARING)")]
    public void OrderingInvariants_IdentityFirst_HousekeepingLast()
    {
        // MigrationTargets.All's doc comment names two load-bearing ordering constraints: Identity
        // must run first (it creates the app_user role every other schema's RLS depends on), and —
        // as of plantry-qszb — Housekeeping must run last (its DeletePackAndDozenUnits migration
        // deletes catalog.units rows only after every other context has relabeled its own pk/doz
        // references; a context appended after it, or a reorder, would delete units still referenced
        // by an un-relabeled schema). This test pins both so a future reorder fails loudly instead of
        // silently reintroducing either bug class.
        Assert.Equal("Plantry.Identity.Infrastructure", MigrationTargets.All[0].MigrationsAssembly);
        // "Plantry.Web" (was "Plantry.Housekeeping.Infrastructure" before ADR-024 Phase A,
        // plantry-g3da.2, relocated HousekeepingDbContext's migrations into the composition root).
        Assert.Equal("Plantry.Web", MigrationTargets.All[^1].MigrationsAssembly);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Plantry.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (Plantry.sln).");
    }
}
