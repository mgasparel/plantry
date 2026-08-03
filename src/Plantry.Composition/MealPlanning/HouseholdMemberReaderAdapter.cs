using Plantry.Identity.Application;
using Plantry.MealPlanning.Application;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Cross-context adapter for <see cref="IHouseholdMemberReader"/> — supplies the MealPlanning context
/// with household-member display facts from the Identity context, over the ASP.NET-free
/// <see cref="IHouseholdDirectory"/> port (plantry-m1u). Lives in Plantry.Composition; the Guid parse is
/// presentation mapping onto MealPlanning's <see cref="HouseholdMember"/> contract, not an Identity
/// concern, so it stays here. The initials computation now lives in the shared
/// <see cref="HouseholdMemberDisplay"/> helper (also used by Recipes' own adapter).
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
