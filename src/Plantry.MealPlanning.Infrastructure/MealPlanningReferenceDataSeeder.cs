using Plantry.Identity.Domain;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Infrastructure;

/// <summary>
/// Seeds default meal slots (Breakfast / Lunch / Dinner) at household creation via the
/// <see cref="IReferenceDataSeeder"/> hook (DM-9), mirroring <c>RecipesReferenceDataSeeder</c>
/// and <c>CatalogReferenceDataSeeder</c>.
/// </summary>
public sealed class MealPlanningReferenceDataSeeder(MealPlanningDbContext db, IClock clock) : IReferenceDataSeeder
{
    public async Task SeedAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        var config = MealSlotConfig.CreateWithDefaults(householdId, clock);

        await db.MealSlotConfigs.AddAsync(config, ct);
        await db.SaveChangesAsync(ct);
    }
}
