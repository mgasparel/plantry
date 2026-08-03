namespace Plantry.Web;

/// <summary>
/// Shared presentation mapping for household-member display facts — the initials shown next to a
/// member's display name (avatars, rating popovers, preference tab strips). Used by every context-local
/// <c>IHouseholdMemberReader</c> adapter (MealPlanning, Recipes) so the same name maps to the same
/// initials everywhere, rather than each adapter carrying its own copy.
/// </summary>
internal static class HouseholdMemberDisplay
{
    internal static string Initials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "?";
        var parts = displayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? parts[0][0].ToString().ToUpperInvariant()
            : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }
}
