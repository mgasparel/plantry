using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// /Settings/Pantry — the household's Inventory settings: the "expiring soon" horizon (plantry-5yhd)
/// and the default storage location (plantry-iypo).
///
/// <para>The "expiring soon" horizon is the number of days within which stock is flagged as expiring
/// soon on the Today widget, the pantry <c>ExpiryTone.Soon</c> badge, and the recipe browse "use soon"
/// filter — one setting, every surface.</para>
///
/// <para>The default storage location is the middle rung in
/// <c>InventoryProducerAdapter.ProduceAsync</c>'s yield-placement fallback chain: it is used to store a
/// cooked recipe yield when the yielded product carries no <c>DefaultLocationId</c> of its own (e.g. a
/// freshly auto-created yield product), instead of the arbitrary alphabetically-first active
/// location.</para>
///
/// GET loads the current values (falling back to the Inventory defaults when unset). POST validates
/// and persists via <see cref="ExpiringSoonSettingsService"/>/<see cref="HouseholdDefaultLocationService"/>,
/// then re-renders with a saved badge. Plain server-rendered form (no JS) per the hypermedia-default UI
/// convention.
/// </summary>
[Authorize]
public sealed class PantryModel(
    ExpiringSoonSettingsService expiringSoonSettings,
    HouseholdDefaultLocationService defaultLocationSettings,
    ILocationRepository locations) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];

    /// <summary>True when a POST persisted successfully — drives the confirmation badge.</summary>
    public bool Saved { get; private set; }

    public sealed class InputModel
    {
        [Display(Name = "Show items as expiring soon within (days)")]
        [Range(
            HouseholdInventorySettings.MinExpiringSoonDays,
            HouseholdInventorySettings.MaxExpiringSoonDays,
            ErrorMessage = "Choose between {1} and {2} days.")]
        public int ExpiringSoonDays { get; set; } = HouseholdInventorySettings.DefaultExpiringSoonDays;

        [Display(Name = "Default storage location")]
        public Guid? DefaultLocationId { get; set; }
    }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        Input.ExpiringSoonDays = await expiringSoonSettings.GetDaysAsync(ct);
        Input.DefaultLocationId = await defaultLocationSettings.GetDefaultLocationIdAsync(ct);
        await LoadLocationOptionsAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            await LoadLocationOptionsAsync(ct);
            return Page();
        }

        // Validate the location up front (existence + active) before writing either field — two
        // independent single-field commands (SetDaysAsync/SetDefaultLocationAsync) would otherwise let
        // a valid days value persist while an invalid location is rejected, leaving a half-applied POST.
        // Delegates to HouseholdDefaultLocationService.ValidateLocationAsync — the single source of
        // truth for this check (also used internally by SetDefaultLocationAsync below) — rather than a
        // second inline copy of the existence/active check, its message, and its rejection log line.
        var locationValidation = await defaultLocationSettings.ValidateLocationAsync(Input.DefaultLocationId, ct);
        if (locationValidation.IsFailure)
        {
            ModelState.AddModelError(
                nameof(Input) + "." + nameof(InputModel.DefaultLocationId), locationValidation.Error.Description);
            await LoadLocationOptionsAsync(ct);
            return Page();
        }

        var daysResult = await expiringSoonSettings.SetDaysAsync(Input.ExpiringSoonDays, ct);
        if (daysResult.IsFailure)
        {
            ModelState.AddModelError(nameof(Input) + "." + nameof(InputModel.ExpiringSoonDays), daysResult.Error.Description);
            await LoadLocationOptionsAsync(ct);
            return Page();
        }

        var locationResult = await defaultLocationSettings.SetDefaultLocationAsync(Input.DefaultLocationId, ct);
        if (locationResult.IsFailure)
        {
            ModelState.AddModelError(nameof(Input) + "." + nameof(InputModel.DefaultLocationId), locationResult.Error.Description);
            await LoadLocationOptionsAsync(ct);
            return Page();
        }

        // Reflect the persisted values (in case of any normalization) and confirm.
        Input.ExpiringSoonDays = await expiringSoonSettings.GetDaysAsync(ct);
        Input.DefaultLocationId = await defaultLocationSettings.GetDefaultLocationIdAsync(ct);
        await LoadLocationOptionsAsync(ct);
        Saved = true;
        return Page();
    }

    private async Task LoadLocationOptionsAsync(CancellationToken ct)
    {
        LocationOptions = (await locations.ListActiveAsync(ct))
            .Select(l => new SelectListItem(l.Name, l.Id.Value.ToString()))
            .ToList();
    }
}
