using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Identity.Domain;

namespace Plantry.Identity.Application;

public sealed class RegisterHouseholdCommand(
    string householdName,
    IClock clock,
    IHouseholdRepository households,
    IEnumerable<IReferenceDataSeeder> seeders,
    TenantContext tenant)
{
    public async Task<Result<HouseholdId>> ExecuteAsync(CancellationToken ct = default)
    {
        var household = Household.Create(householdName, clock);
        await households.AddAsync(household, ct);
        await households.SaveChangesAsync(ct);

        // Arm the tenant GUC only while seeding the new household's reference data: the catalog
        // RLS policy is strict, so those inserts require app.household_id to match. Cleared
        // afterwards so the caller's user creation runs without household scoping (global email
        // uniqueness, and the identity policy's no-context carve-out permits the user insert).
        tenant.Set(household.Id.Value);
        try
        {
            foreach (var seeder in seeders)
                await seeder.SeedAsync(household.Id, ct);
        }
        finally
        {
            tenant.Clear();
        }

        return Result<HouseholdId>.Success(household.Id);
    }
}
