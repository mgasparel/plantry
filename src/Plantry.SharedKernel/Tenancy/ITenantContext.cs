namespace Plantry.SharedKernel.Tenancy;

/// <summary>
/// Ambient, request-scoped tenant (household) for the current unit of work.
/// Read by the EF query filters and by the RLS connection interceptor so both
/// layers of household isolation observe the same tenant.
/// </summary>
public interface ITenantContext
{
    Guid? HouseholdId { get; }
}
