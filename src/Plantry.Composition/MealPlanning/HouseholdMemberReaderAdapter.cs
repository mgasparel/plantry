using Plantry.Identity.Application;
using Plantry.MealPlanning.Application;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Cross-context adapter for <see cref="IHouseholdMemberReader"/> — supplies the MealPlanning context
/// with household-member display facts from the Identity context, over the ASP.NET-free
/// <see cref="IHouseholdDirectory"/> port (plantry-m1u). Lives in Plantry.Composition; the Guid parse +
/// Initials computation are presentation mapping onto MealPlanning's <see cref="HouseholdMember"/>
/// contract, not an Identity concern, so they stay here.
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
            Initials(m.DisplayName)
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
