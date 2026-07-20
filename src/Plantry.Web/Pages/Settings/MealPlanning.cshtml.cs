using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Application;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// /Settings/MealPlanning — household-default planning settings (budget + weights).
/// GET: loads the stored household default and renders the form pre-populated with current values.
/// POST ?handler=SetMealPlanningDefaults: persists the household default via
/// <see cref="SetPlanningSettingsService"/> with weekStart: null (household default, not a
/// per-week override). Returns the updated form fragment so the page reflects the saved values.
/// </summary>
[Authorize]
public sealed class MealPlanningModel(
    IHouseholdPlanningSettingsRepository settingsRepo,
    SetPlanningSettingsService setPlanningSettingsService,
    IDisplayCurrency displayCurrency,
    ITenantContext tenant) : PageModel
{
    /// <summary>Current household default budget in decimal dollars, or null when not set.</summary>
    public decimal? DefaultBudget { get; private set; }

    /// <summary>Current household default planning weights, or null when not set.</summary>
    public PlanningWeights? DefaultWeights { get; private set; }

    /// <summary>True when the POST handler saved successfully — used to show a confirmation badge.</summary>
    public bool Saved { get; private set; }

    public async Task OnGetAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    /// <summary>
    /// htmx fragment handler: persists the household default budget and/or weights.
    /// weekStart is null — this updates the household default, not a per-week override.
    /// Returns the updated settings form partial so the page reflects the saved values.
    /// </summary>
    public async Task<IActionResult> OnPostSetMealPlanningDefaultsAsync(
        [FromForm] decimal? budget,
        [FromForm] int? wasteWeight,
        [FromForm] int? costWeight,
        [FromForm] int? varietyWeight,
        CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);

        // Resolve submitted budget: positive value → Money stamped with the household's display
        // currency (plantry-2x6e.1); zero or null → clear.
        Money? budgetMoney = budget is > 0
            ? Money.FromDecimal(budget.Value, await displayCurrency.GetAsync(ct))
            : null;

        // Resolve submitted weights; ignore if they don't sum to 100.
        PlanningWeights? weights = null;
        if (wasteWeight.HasValue && costWeight.HasValue && varietyWeight.HasValue)
        {
            try { weights = new PlanningWeights(wasteWeight.Value, costWeight.Value, varietyWeight.Value); }
            catch (ArgumentException) { /* Invalid — fall back to null (no override) */ }
        }

        // weekStart: null → updates household default (not a per-week override).
        await setPlanningSettingsService.ExecuteAsync(householdId, weekStart: null, budgetMoney, weights, ct);

        await LoadAsync(ct);
        Saved = true;
        return Partial("Settings/_MealPlanningDefaults", this);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var settings = await settingsRepo.FindByHouseholdAsync(householdId, ct);
        DefaultBudget = settings?.DefaultWeeklyBudget?.ToDecimal();
        DefaultWeights = settings?.DefaultPlanningWeights;
    }
}
