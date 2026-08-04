using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plantry.Planning.Infrastructure;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct <see cref="MealPlanningDbContext"/>
/// without booting the Aspire web host.
/// </summary>
public sealed class MealPlanningDbContextFactory : IDesignTimeDbContextFactory<MealPlanningDbContext>
{
    public MealPlanningDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MealPlanningDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=plantry_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Plantry.Planning.Infrastructure"))
            .Options;

        return new MealPlanningDbContext(options);
    }
}
