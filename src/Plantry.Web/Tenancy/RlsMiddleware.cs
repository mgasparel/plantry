using Plantry.Catalog.Infrastructure;
using Plantry.Deals.Infrastructure;
using Plantry.Housekeeping.Infrastructure;
using Plantry.Identity.Infrastructure;
using Plantry.Intake.Infrastructure;
using Plantry.Inventory.Infrastructure;
using Plantry.MealPlanning.Infrastructure;
using Plantry.Pricing.Infrastructure;
using Plantry.Recipes.Infrastructure;
using Plantry.Shopping.Infrastructure;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Tenancy;

/// <summary>
/// For every authenticated request, resolves the HouseholdId from the principal and applies it
/// to both layers of household isolation:
///   1. <see cref="TenantContext"/>, which the RLS connection interceptor reads to set the
///      Postgres <c>app.household_id</c> session GUC on the live connection (database backstop).
///   2. <see cref="CatalogDbContext.SetHouseholdId"/>, which feeds the EF query filter (app layer).
/// Both must be live for defense-in-depth; relying on either alone is a tenant-isolation bug.
///
/// CRITICAL: Every bounded-context DbContext must be registered here (the known P2-0 / P3-0 gotcha).
/// Omitting a context leaves its _householdId as Guid.Empty, so the EF query filter returns nothing
/// while writes silently succeed — a silent data loss / isolation bug.
/// </summary>
public sealed class RlsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, TenantContext tenant, CatalogDbContext catalogDb,
        PlantryIdentityDbContext identityDb, InventoryDbContext inventoryDb, IntakeDbContext intakeDb,
        RecipesDbContext recipesDb, ShoppingDbContext shoppingDb, MealPlanningDbContext mealPlanningDb,
        PricingDbContext pricingDb, DealsDbContext dealsDb, HousekeepingDbContext housekeepingDb)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var hid = HouseholdIdClaims.TryResolve(context.User);
            if (hid.HasValue)
            {
                var id = hid.Value.Value;
                tenant.Set(id);                       // arms Postgres RLS via the connection interceptor
                catalogDb.SetHouseholdId(id);         // feeds the Catalog EF query filter
                identityDb.SetHouseholdId(id);        // feeds the Household EF query filter
                inventoryDb.SetHouseholdId(id);       // feeds the Inventory EF query filter
                intakeDb.SetHouseholdId(id);          // feeds the Intake EF query filter
                recipesDb.SetHouseholdId(id);         // feeds the Recipes EF query filter
                shoppingDb.SetHouseholdId(id);        // feeds the Shopping EF query filter
                mealPlanningDb.SetHouseholdId(id);    // feeds the MealPlanning EF query filter
                pricingDb.SetHouseholdId(id);         // feeds the Pricing EF query filter
                dealsDb.SetHouseholdId(id);           // feeds the Deals EF query filter (P5-0)
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
