using Plantry.Identity.Application;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// Cross-context adapter for Recipes' <see cref="IHouseholdMemberReader"/> — supplies the Recipes
/// context with household-member display facts from the Identity context, over the ASP.NET-free
/// <see cref="IHouseholdDirectory"/> port (plantry-m1u), for the per-rating-member breakdown popover
/// (plantry-zlwp.1). A second, independent copy of the same adapter shape as
/// <c>Plantry.Web.MealPlanning.HouseholdMemberReaderAdapter</c> (DM-3 — Recipes owns its own ACL port
/// rather than depending on MealPlanning's Application layer). The initials computation is shared via
/// <see cref="HouseholdMemberDisplay"/> rather than duplicated here.
/// </summary>
public sealed class HouseholdMemberReaderAdapter(
    IHouseholdDirectory directory) : IHouseholdMemberReader
{
    public async Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default)
    {
        var members = await directory.ListMembersAsync(ct);

        return members.Select(m => new HouseholdMember(
            Guid.Parse(m.UserId),
            m.DisplayName,
            HouseholdMemberDisplay.Initials(m.DisplayName)
        )).ToList();
    }
}
