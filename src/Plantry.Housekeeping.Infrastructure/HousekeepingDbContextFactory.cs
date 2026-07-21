using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plantry.Housekeeping.Infrastructure;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct <see cref="HousekeepingDbContext"/>
/// without booting the Aspire web host.
/// </summary>
public sealed class HousekeepingDbContextFactory : IDesignTimeDbContextFactory<HousekeepingDbContext>
{
    public HousekeepingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=plantry_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Plantry.Housekeeping.Infrastructure"))
            .Options;

        return new HousekeepingDbContext(options);
    }
}
