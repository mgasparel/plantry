using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;

namespace Plantry.Identity.Infrastructure;

public sealed class HouseholdInviteRepository(PlantryIdentityDbContext db) : IHouseholdInviteRepository
{
    public async Task AddAsync(HouseholdInvite invite, CancellationToken ct = default) =>
        await db.HouseholdInvites.AddAsync(invite, ct);

    public Task<HouseholdInvite?> FindByIdAsync(HouseholdInviteId id, CancellationToken ct = default) =>
        db.HouseholdInvites.FirstOrDefaultAsync(i => i.Id == id, ct);

    // No tenant context: the invitee is unauthenticated, so the per-household EF query filter is lifted
    // (IgnoreQueryFilters) and the lookup relies on the identity.household_invites RLS no-context
    // carve-out — all rows are visible only while app.household_id is unset. The token is a high-entropy
    // secret, so an exact-match lookup is the only surface. See IHouseholdInviteRepository.FindByTokenAsync.
    public Task<HouseholdInvite?> FindByTokenAsync(string token, CancellationToken ct = default) =>
        db.HouseholdInvites
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Token == token, ct);

    // Tenant-scoped: the household-scoped EF query filter on HouseholdInvites (and the RLS policy)
    // restrict this to the active household's invites. Most-recently-issued first.
    public async Task<IReadOnlyList<HouseholdInvite>> ListPendingAsync(CancellationToken ct = default) =>
        await db.HouseholdInvites
            .Where(i => i.Status == InviteStatus.Pending)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

    // Translate EF's provider-specific optimistic-concurrency failure (the xmin-guarded accept/revoke
    // UPDATE matching zero rows because a concurrent transaction won the race) into the EF-free
    // SharedKernel exception the application service reacts to. All other persistence failures propagate.
    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(
                "A concurrent transaction changed this household_invite before this write landed.", ex);
        }
    }
}
