using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// /Settings/Pantry — the household's Inventory settings. Currently exposes the single
/// "expiring soon" horizon (plantry-5yhd): the number of days within which stock is flagged as
/// expiring soon on the Today widget, the pantry <c>ExpiryTone.Soon</c> badge, and the recipe
/// browse "use soon" filter — one setting, every surface.
///
/// GET loads the current value (falling back to the Inventory default when unset). POST validates
/// and persists via <see cref="ExpiringSoonSettingsService"/>, then re-renders with a saved badge.
/// Plain server-rendered form (no JS) per the hypermedia-default UI convention.
/// </summary>
[Authorize]
public sealed class PantryModel(ExpiringSoonSettingsService settings) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

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
    }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        Input.ExpiringSoonDays = await settings.GetDaysAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await settings.SetDaysAsync(Input.ExpiringSoonDays, ct);
        if (result.IsFailure)
        {
            ModelState.AddModelError(nameof(Input) + "." + nameof(InputModel.ExpiringSoonDays), result.Error.Description);
            return Page();
        }

        // Reflect the persisted value (in case of any normalization) and confirm.
        Input.ExpiringSoonDays = await settings.GetDaysAsync(ct);
        Saved = true;
        return Page();
    }
}
