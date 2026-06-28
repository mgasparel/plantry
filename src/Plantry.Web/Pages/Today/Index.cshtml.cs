using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Intake.Domain;
using Plantry.Recipes.Application;
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
    InventoryQueryService inventoryQueries,
    BrowseRecipesQuery browseRecipes,
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

    /// <summary>
    /// Products expiring within the next <see cref="InventoryQueryService.ExpiringSoonDays"/> days
    /// (or already expired), ordered soonest-first. Empty when <see cref="IsColdStart"/> is true
    /// or the household has no stock nearing expiry.
    /// </summary>
    public IReadOnlyList<ExpiringSoonItem> ExpiringSoon { get; private set; } = [];

    /// <summary>
    /// Up to 3 cook-now recipe picks for the meals band (SPEC Page 0 §0c, plantry-81g).
    /// Sorted by expiring-ingredient first (use-it-up angle), then by fulfillment percentage
    /// descending. Empty when <see cref="IsColdStart"/> is true or the household has no recipes.
    /// Null when the query has not yet been executed (before <see cref="OnGetAsync"/> completes).
    /// </summary>
    public IReadOnlyList<RecipeBrowseRow> CookNowPicks { get; private set; } = [];

    /// <summary>Number of cook-now picks to display on the Today page.</summary>
    internal const int CookNowPickCount = 3;

    /// <summary>
    /// True when the expiring-soon badge should render in the urgent tone (at least one item
    /// with 0 or 1 day remaining, including expired lots).
    /// </summary>
    public bool ExpiringUrgent => ExpiringSoon.Any(x => x.DaysLeft <= 1);

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

            var hasStock = await stocks.AnyForHouseholdAsync(houseId, ct);
            var hasRecipes = await recipes.AnyForHouseholdAsync(houseId, ct);
            var hasPendingIntake = await sessions.HasPendingAsync(houseId, ct);

            IsColdStart = !hasStock && !hasRecipes && !hasPendingIntake;
            ShowTakeStockCta = !hasStock;

            if (!IsColdStart)
            {
                ExpiringSoon = await inventoryQueries.ExpiringSoonAsync(ct);
                CookNowPicks = await LoadCookNowPicksAsync(ct);
            }
        }
        else
        {
            Greeting = BuildGreeting(now.LocalDateTime.Hour, string.Empty);
            IsColdStart = true;
            ShowTakeStockCta = true;
        }
    }

    /// <summary>
    /// Loads the cook-now recipe picks for the Today meals band.
    /// Runs the full <see cref="BrowseRecipesQuery"/> (all recipes, fulfillment sort), then picks
    /// the top <see cref="CookNowPickCount"/> rows sorted by:
    ///   1. HasIngredientExpiringSoon descending (use-it-up angle — expiring picks first)
    ///   2. FulfillmentPct descending (most cookable first within each tier)
    /// </summary>
    private async Task<IReadOnlyList<RecipeBrowseRow>> LoadCookNowPicksAsync(CancellationToken ct)
    {
        var result = await browseRecipes.ExecuteAsync(new BrowseRecipesFilter(), ct);
        return SelectCookNowPicks(result.Rows, CookNowPickCount);
    }

    /// <summary>
    /// Applies the cook-now pick selection logic: expiring-ingredient recipes first (use-it-up),
    /// then fulfillment percentage descending, capped at <paramref name="maxPicks"/>.
    /// Extracted as a static helper for deterministic unit testing without database access.
    /// </summary>
    internal static IReadOnlyList<RecipeBrowseRow> SelectCookNowPicks(
        IReadOnlyList<RecipeBrowseRow> rows, int maxPicks = CookNowPickCount) =>
        rows
            .OrderByDescending(r => r.HasIngredientExpiringSoon)
            .ThenByDescending(r => r.FulfillmentPct)
            .Take(maxPicks)
            .ToList();

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
