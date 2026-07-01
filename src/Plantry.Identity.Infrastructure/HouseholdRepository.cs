using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;

namespace Plantry.Identity.Infrastructure;

public sealed class HouseholdRepository(PlantryIdentityDbContext db) : IHouseholdRepository
{
    public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default) =>
        db.Households.FirstOrDefaultAsync(h => h.Id == id, ct);

    // Cross-tenant: bypasses the per-household EF query filter so the ingestion worker can discover every
    // household. IgnoreQueryFilters lifts the app-layer filter; the Postgres households RLS policy still
    // guards the row set and only exposes all rows when app.household_id is unset (see the port contract).
    public async Task<IReadOnlyList<HouseholdId>> ListAllIdsAsync(CancellationToken ct = default) =>
        await db.Households
            .IgnoreQueryFilters()
            .Select(h => h.Id)
            .ToListAsync(ct);

    public async Task AddAsync(Household household, CancellationToken ct = default) =>
        await db.Households.AddAsync(household, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
