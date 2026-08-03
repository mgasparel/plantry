namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption read port onto the Identity context for household-member display facts — the same
/// seam the Members page and MealPlanning's own <c>IHouseholdMemberReader</c> use, defined locally here
/// (rather than depending on MealPlanning's copy) so Recipes stays free of a sibling-context Application
/// dependency (DDD bounded-context discipline — every other Recipes cross-context read
/// (<c>ICatalogProductReader</c>, <c>IInventoryStockReader</c>, ...) is its own locally-defined port).
/// Implemented in Plantry.Composition over <c>IHouseholdDirectory</c> (plantry-zlwp.1).
/// </summary>
public interface IHouseholdMemberReader
{
    /// <summary>Returns all members of the signed-in household, ordered by display name.</summary>
    Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default);
}

/// <summary>Display facts for a household member — used by the per-rating-member breakdown popover.</summary>
public sealed record HouseholdMember(Guid UserId, string DisplayName, string Initials);
