using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Infrastructure;
using Plantry.Intake.Infrastructure;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Tenancy;

/// <summary>
/// For every authenticated request, resolves the HouseholdId from the principal and applies it
/// to both layers of household isolation:
///   1. <see cref="TenantContext"/>, which the RLS connection interceptor reads to set the
///      Postgres <c>app.household_id</c> session GUC on the live connection (database backstop).
///   2. <see cref="CatalogDbContext.SetHouseholdId"/>, which feeds the EF query filter (app layer).
/// Both must be live for defense-in-depth; relying on either alone is a tenant-isolation bug.
/// </summary>
public sealed class RlsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, TenantContext tenant, CatalogDbContext catalogDb,
        PlantryIdentityDbContext identityDb, InventoryDbContext inventoryDb, IntakeDbContext intakeDb)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var hid = HouseholdIdClaims.TryResolve(context.User);
            if (hid.HasValue)
            {
                var id = hid.Value.Value;
                tenant.Set(id);                 // arms Postgres RLS via the connection interceptor
                catalogDb.SetHouseholdId(id);   // feeds the Catalog EF query filter
                identityDb.SetHouseholdId(id);  // feeds the Household EF query filter
                inventoryDb.SetHouseholdId(id); // feeds the Inventory EF query filter
                intakeDb.SetHouseholdId(id);    // feeds the Intake EF query filter
                // Additional contexts (Pricing, Shopping, etc.) added here in later slices
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
