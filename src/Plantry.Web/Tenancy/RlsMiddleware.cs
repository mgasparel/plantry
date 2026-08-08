using Plantry.Pantry.Infrastructure;
using Plantry.Market.Infrastructure;
using Plantry.Identity.Infrastructure;
using Plantry.Intake.Infrastructure;
using Plantry.Planning.Infrastructure;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;

namespace Plantry.Web.Tenancy;

/// <summary>
/// For every authenticated request, resolves the HouseholdId from the principal and applies it
/// to both layers of household isolation:
///   1. <see cref="TenantContext"/>, which the RLS connection interceptor reads to set the
///      Postgres <c>app.household_id</c> session GUC on the live connection (database backstop).
///   2. <see cref="PantryDbContext.SetHouseholdId"/>, which feeds the EF query filter (app layer).
/// Both must be live for defense-in-depth; relying on either alone is a tenant-isolation bug.
///
/// CRITICAL: Every bounded-context DbContext must be registered here (the known P2-0 / P3-0 gotcha).
/// Omitting a context leaves its _householdId as Guid.Empty, so the EF query filter returns nothing
/// while writes silently succeed — a silent data loss / isolation bug.
/// </summary>
public sealed class RlsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, TenantContext tenant, PantryDbContext pantryDb,
        PlantryIdentityDbContext identityDb, IntakeDbContext intakeDb,
        RecipesDbContext recipesDb, ShoppingDbContext shoppingDb, MealPlanningDbContext mealPlanningDb,
        MarketDbContext marketDb, HousekeepingDbContext housekeepingDb)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var hid = HouseholdIdClaims.TryResolve(context.User);
            if (hid.HasValue)
            {
                var id = hid.Value.Value;
                tenant.Set(id);                       // arms Postgres RLS via the connection interceptor
                pantryDb.SetHouseholdId(id);           // feeds the Pantry (catalog + inventory) EF query filter (plantry-g3da.10)
                identityDb.SetHouseholdId(id);        // feeds the Household EF query filter
                intakeDb.SetHouseholdId(id);          // feeds the Intake EF query filter
                recipesDb.SetHouseholdId(id);         // feeds the Recipes EF query filter
                shoppingDb.SetHouseholdId(id);        // feeds the Shopping EF query filter
                mealPlanningDb.SetHouseholdId(id);    // feeds the MealPlanning EF query filter
                marketDb.SetHouseholdId(id);           // feeds the Market (pricing + deals) EF query filter (P5-0, plantry-g3da.7)
                housekeepingDb.SetHouseholdId(id);    // feeds the Housekeeping EF query filter (tidy-up.md)
            }
        }

        await next(context);
    }
}

public static class RlsMiddlewareExtensions
{
    public static IApplicationBuilder UseRls(this IApplicationBuilder app) =>
        app.UseMiddleware<RlsMiddleware>();
}
