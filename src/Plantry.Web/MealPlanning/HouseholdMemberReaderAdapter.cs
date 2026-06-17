using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IHouseholdMemberReader"/> — supplies the MealPlanning context
/// with household-member display facts from the Identity context, over
/// <see cref="UserManager{AppUser}"/>. Lives in Plantry.Web to keep MealPlanning free of Identity deps.
/// </summary>
public sealed class HouseholdMemberReaderAdapter(
    UserManager<AppUser> userManager,
    ITenantContext tenant) : IHouseholdMemberReader
{
    public async Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default)
    {
        var householdId = tenant.HouseholdId;
        if (householdId is null) return [];

        var users = await userManager.Users
            .Where(u => u.HouseholdId == householdId.Value)
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct);

        return users.Select(u => new HouseholdMember(
            Guid.Parse(u.Id),
            u.DisplayName,
            Initials(u.DisplayName)
        )).ToList();
    }

    private static string Initials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "?";
        var parts = displayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? displayName[0].ToString().ToUpperInvariant()
            : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }
}
