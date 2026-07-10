namespace Plantry.Identity.Domain;

public interface IHouseholdInviteRepository
{
    Task AddAsync(HouseholdInvite invite, CancellationToken ct = default);

    /// <summary>
    /// Finds an invite by id within the current tenant (the household-scoped EF query filter and the
    /// Postgres RLS policy both apply). Returns null when no such invite belongs to the active household.
    /// The sanctioned path for the authenticated issue/revoke operations.
    /// </summary>
    Task<HouseholdInvite?> FindByIdAsync(HouseholdInviteId id, CancellationToken ct = default);

    /// <summary>
    /// <b>No tenant context.</b> Resolves an invite by its globally unique token, bypassing the
    /// per-household EF query filter. This is the accept path: the invitee is unauthenticated and no
    /// household is armed, so the lookup relies on the <c>identity.household_invites</c> RLS no-context
    /// carve-out (all rows visible only when <c>app.household_id</c> is unset — the same carve-out that
    /// lets ASP.NET Core Identity find a user pre-auth). Callers MUST invoke this with no
    /// <c>TenantContext</c> armed; run inside an armed household it collapses to that one household's
    /// invites. The token is a high-entropy secret, so an exact-match lookup is the only exposure.
    /// </summary>
    Task<HouseholdInvite?> FindByTokenAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Lists the current household's pending invites, most-recently-issued first. Tenant-scoped: the
    /// household-scoped EF query filter (and the Postgres RLS policy) apply, so only the active
    /// household's invites are returned. Feeds the Settings &gt; Members roster of outstanding invites.
    /// </summary>
    Task<IReadOnlyList<HouseholdInvite>> ListPendingAsync(CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
