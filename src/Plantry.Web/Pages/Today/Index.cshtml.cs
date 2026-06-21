using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Domain;
using Plantry.Inventory.Domain;
using Plantry.Intake.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Today;

[Authorize]
public sealed class IndexModel(
    IHouseholdRepository households,
    IProductStockRepository stocks,
    IRecipeRepository recipes,
    IImportSessionRepository sessions,
    IClock clock,
    ITenantContext tenant) : PageModel
{
    /// <summary>The greeting text, e.g. "Good morning, Rivera household."</summary>
    public string Greeting { get; private set; } = string.Empty;

    /// <summary>Household name for the date line, e.g. "Rivera household".</summary>
    public string HouseholdName { get; private set; } = string.Empty;

    /// <summary>Formatted date string for the date line, e.g. "Thursday, June 18".</summary>
    public string DateDisplay { get; private set; } = string.Empty;

    /// <summary>
    /// True when the household has no stock, no recipes, and no pending intake sessions —
    /// triggers the Welcome hero instead of empty widget placeholders.
    /// </summary>
    public bool IsColdStart { get; private set; }

    /// <summary>
    /// True when the household has no tracked stock — surfaces the Take Stock CTA so
    /// the user is guided to /pantry/take-stock to populate the pantry (J6).
    /// Recedes once any stock exists; independent of IsColdStart so the CTA is shown
    /// even when the household has recipes but an empty pantry.
    /// </summary>
    public bool ShowTakeStockCta { get; private set; }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        DateDisplay = now.LocalDateTime.ToString("dddd, MMMM d");

        HouseholdId? householdId = tenant.HouseholdId is { } hid
            ? HouseholdId.From(hid)
            : null;

        if (householdId is { } houseId)
        {
            var household = await households.FindAsync(houseId, ct);
            HouseholdName = household?.Name ?? string.Empty;

            Greeting = BuildGreeting(now.LocalDateTime.Hour, HouseholdName);

            var allStock = await stocks.ListForHouseholdAsync(houseId, ct);
            var hasStock = allStock.Count > 0;

            var allRecipes = await recipes.ListForBrowseAsync(ct);
            var hasRecipes = allRecipes.Count > 0;

            var pendingSessions = await sessions.ListPendingAsync(houseId, ct);
            var hasPendingIntake = pendingSessions.Count > 0;

            IsColdStart = !hasStock && !hasRecipes && !hasPendingIntake;
            ShowTakeStockCta = !hasStock;
        }
        else
        {
            Greeting = BuildGreeting(now.LocalDateTime.Hour, string.Empty);
            IsColdStart = true;
            ShowTakeStockCta = true;
        }
    }

    /// <summary>Returns a greeting appropriate to the local hour.</summary>
    internal static string BuildGreeting(int hour, string householdName)
    {
        var salutation = hour switch
        {
            >= 5 and < 12  => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            >= 17 and < 21 => "Good evening",
            _              => "Good night",
        };

        return string.IsNullOrWhiteSpace(householdName)
            ? $"{salutation}."
            : $"{salutation}, {householdName}.";
    }
}
