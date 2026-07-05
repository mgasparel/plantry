using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Application;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Identity.Infrastructure;

/// <summary>
/// ASP.NET-Identity-backed implementation of <see cref="IHouseholdDirectory"/> (plantry-m1u). Reads the
/// current household's users over <see cref="UserManager{AppUser}"/>, filtered by the request-scoped
/// <see cref="ITenantContext"/>. This is the sole home for the ASP.NET Identity dependency behind the port
/// — the query moved here verbatim from the former Plantry.Web HouseholdMemberReaderAdapter, so tenancy
/// behaviour is unchanged (returns [] when there is no tenant household).
/// </summary>
public sealed class HouseholdDirectory(
    UserManager<AppUser> userManager,
    ITenantContext tenant) : IHouseholdDirectory
{
    public async Task<IReadOnlyList<HouseholdUser>> ListMembersAsync(CancellationToken ct = default)
    {
        var householdId = tenant.HouseholdId;
        if (householdId is null) return [];

        var users = await userManager.Users
            .Where(u => u.HouseholdId == householdId.Value)
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct);

        return users.Select(u => new HouseholdUser(u.Id, u.DisplayName)).ToList();
    }
}
