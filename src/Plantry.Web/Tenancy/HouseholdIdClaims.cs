using System.Security.Claims;
using Plantry.SharedKernel;

namespace Plantry.Web.Tenancy;

public static class HouseholdIdClaims
{
    public const string ClaimType = "household_id";

    public static HouseholdId? TryResolve(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimType);
        if (value is null) return null;
        if (!Guid.TryParse(value, out var guid)) return null;
        return HouseholdId.From(guid);
    }

    public static HouseholdId Resolve(ClaimsPrincipal user) =>
        TryResolve(user) ?? throw new InvalidOperationException(
            "Authenticated user has no household_id claim. Ensure the claim is set on registration.");
}
