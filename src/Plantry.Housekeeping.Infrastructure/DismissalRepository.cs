using Microsoft.EntityFrameworkCore;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;

namespace Plantry.Housekeeping.Infrastructure;

/// <summary>
/// EF-backed repository for the <see cref="Dismissal"/> aggregate. All queries run through
/// <see cref="HousekeepingDbContext"/>'s household query filter (RLS-scoped), so reads never cross tenants.
/// </summary>
public sealed class DismissalRepository(HousekeepingDbContext db) : IDismissalRepository
{
    public async Task<IReadOnlyList<Dismissal>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        await db.Dismissals.Where(d => d.HouseholdId == householdId).ToListAsync(ct);

    public Task<Dismissal?> FindAsync(
        HouseholdId householdId, DetectorId detectorId, Guid subjectId, CancellationToken ct = default) =>
        db.Dismissals.FirstOrDefaultAsync(
            d => d.HouseholdId == householdId && d.DetectorId == detectorId && d.SubjectId == subjectId, ct);

    public async Task AddAsync(Dismissal dismissal, CancellationToken ct = default) =>
        await db.Dismissals.AddAsync(dismissal, ct);

    public Task RemoveAsync(Dismissal dismissal, CancellationToken ct = default)
    {
        db.Dismissals.Remove(dismissal);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
