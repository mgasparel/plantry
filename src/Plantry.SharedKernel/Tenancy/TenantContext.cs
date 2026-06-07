namespace Plantry.SharedKernel.Tenancy;

/// <summary>
/// Mutable, scoped implementation of <see cref="ITenantContext"/>. Set by the RLS
/// middleware for authenticated requests, and transiently by registration while it
/// seeds a brand-new household's reference data (before any user is signed in).
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? HouseholdId { get; private set; }

    public void Set(Guid householdId) => HouseholdId = householdId;

    public void Clear() => HouseholdId = null;
}
