using Plantry.SharedKernel;

namespace Plantry.Identity.Domain;

public interface IHouseholdRepository
{
    Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default);

    /// <summary>
    /// <b>Cross-tenant.</b> Every household's id, ignoring the per-household EF query filter. The only
    /// sanctioned use is the background ingestion worker's cross-tenant sweep (P5-6 / DJ2), which has no
    /// HTTP principal and must discover every household before arming tenancy per household.
    /// <para>
    /// <b>Tenancy contract:</b> the Postgres <c>identity.households</c> RLS policy still applies — it
    /// returns all rows <b>only when <c>app.household_id</c> is unset</b> (the pre-auth carve-out). Callers
    /// MUST invoke this with no <c>TenantContext</c> armed; run inside an armed household it collapses to
    /// that one household. Never call it from a request-scoped, tenant-armed path.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<HouseholdId>> ListAllIdsAsync(CancellationToken ct = default);

    Task AddAsync(Household household, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
