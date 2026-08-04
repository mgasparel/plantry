using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plantry.Market.Infrastructure;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct <see cref="MarketDbContext"/>
/// without booting the Aspire web host. The connection string is a placeholder — scaffolding a
/// migration only needs the provider wired up; it never opens a connection.
/// </summary>
public sealed class MarketDbContextFactory : IDesignTimeDbContextFactory<MarketDbContext>
{
    public MarketDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MarketDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=plantry_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Plantry.Market.Infrastructure"))
            .Options;

        return new MarketDbContext(options);
    }
}
