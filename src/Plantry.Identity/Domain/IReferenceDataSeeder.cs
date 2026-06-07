using Plantry.SharedKernel;

namespace Plantry.Identity.Domain;

/// <summary>
/// Port called by the Identity application service after a Household is created.
/// Each bounded-context Infrastructure implements this to seed its own reference data.
/// </summary>
public interface IReferenceDataSeeder
{
    Task SeedAsync(HouseholdId householdId, CancellationToken ct = default);
}
