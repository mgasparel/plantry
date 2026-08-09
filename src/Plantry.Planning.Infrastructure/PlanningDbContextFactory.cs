using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plantry.Planning.Infrastructure;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct <see cref="PlanningDbContext"/>
/// without booting the Aspire web host.
/// </summary>
public sealed class PlanningDbContextFactory : IDesignTimeDbContextFactory<PlanningDbContext>
{
    public PlanningDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlanningDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=plantry_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Plantry.Planning.Infrastructure"))
            .Options;

        return new PlanningDbContext(options);
    }
}
