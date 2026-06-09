using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plantry.Inventory.Infrastructure;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct <see cref="InventoryDbContext"/>
/// without booting the Aspire web host. The connection string is a placeholder — scaffolding a
/// migration only needs the provider wired up; it never opens a connection.
/// </summary>
public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=plantry_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Plantry.Inventory.Infrastructure"))
            .Options;

        return new InventoryDbContext(options);
    }
}
