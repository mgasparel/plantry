using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Anti-corruption adapter: reads household members from Identity for the attendee picker.
/// Lives in Plantry.Web (the composition root) so MealPlanning.Application stays free of Identity.
/// </summary>
public sealed class HouseholdMemberReaderAdapter(UserManager<AppUser> userManager) : IHouseholdMemberReader
{
    public async Task<IReadOnlyList<HouseholdMember>> GetMembersAsync(
        Guid householdId,
        CancellationToken ct = default)
    {
        var users = await userManager.Users
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new HouseholdMember(Guid.Parse(u.Id), u.DisplayName))
            .ToListAsync(ct);
        return users;
    }
}
