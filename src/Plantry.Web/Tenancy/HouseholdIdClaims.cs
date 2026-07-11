using System.Security.Claims;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel;

namespace Plantry.Web.Tenancy;

public static class HouseholdIdClaims
{
    // Single source of truth lives in the Identity module (the writer of this claim). Aliased here so
    // the web tier's readers/callers keep using HouseholdIdClaims.ClaimType unchanged.
    public const string ClaimType = HouseholdClaimTypes.HouseholdId;

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
