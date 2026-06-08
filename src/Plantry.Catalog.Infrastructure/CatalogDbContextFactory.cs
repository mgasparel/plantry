using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plantry.Catalog.Infrastructure;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct <see cref="CatalogDbContext"/>
/// without booting the Aspire web host. The connection string is a placeholder — scaffolding a
/// migration only needs the provider wired up; it never opens a connection.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=plantry_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Plantry.Catalog.Infrastructure"))
            .Options;

        return new CatalogDbContext(options);
    }
}
