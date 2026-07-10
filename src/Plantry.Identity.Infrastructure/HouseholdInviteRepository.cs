using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Domain;

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

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
