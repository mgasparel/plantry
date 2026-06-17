namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto the Identity context for household-member display facts.
/// Implemented in Plantry.Web over <c>UserManager&lt;AppUser&gt;</c>.
/// </summary>
public interface IHouseholdMemberReader
{
    /// <summary>
    /// Returns all members of the signed-in household, ordered by display name.
    /// </summary>
    Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default);
}

/// <summary>Display facts for a household member in the preferences tab strip.</summary>
public sealed record HouseholdMember(Guid UserId, string DisplayName, string Initials);
