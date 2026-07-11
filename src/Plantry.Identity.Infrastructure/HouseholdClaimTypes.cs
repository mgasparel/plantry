namespace Plantry.Identity.Infrastructure;

/// <summary>
/// Claim types the Identity module stamps onto an <see cref="AppUser"/>. The household claim is baked
/// into the auth cookie on registration/join and drives RLS for every subsequent authenticated request.
/// Single source of truth: the join saga (<see cref="JoinHouseholdCommand"/>) stamps it inside the
/// transaction, and the web tier's <c>HouseholdIdClaims</c> reader resolves it back — both reference
/// this constant so the claim type can never drift between writer and reader.
/// </summary>
public static class HouseholdClaimTypes
{
    public const string HouseholdId = "household_id";
}
