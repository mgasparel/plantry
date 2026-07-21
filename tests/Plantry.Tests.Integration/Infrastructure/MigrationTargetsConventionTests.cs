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
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

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
