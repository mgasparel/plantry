using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Application;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// /Settings/Expiry — the household's expiry defaults (plantry-qckx): the after-freezing and
/// after-thawing due-days defaults added by plantry-hh1f.
///
/// GET loads the two current values (falling back to the household-less defaults 90/3 when unset).
/// POST validates each field to <see cref="HouseholdExpiryDefaultsService.MinDays"/>–
/// <see cref="HouseholdExpiryDefaultsService.MaxDays"/> (client-side via <see cref="RangeAttribute"/>,
/// server-side again in the service) and persists via <see cref="HouseholdExpiryDefaultsService"/>,
/// then re-renders with a saved badge. Plain server-rendered form (no JS island) per the
/// hypermedia-default UI convention (ADR-020), mirroring /Settings/Pantry and /Settings/Currency.
///
/// This page deliberately does NOT offer an expiry-warning input — the household's previously
/// unreachable, never-consumed per-row "expiry warning days" column was retired entirely
/// (plantry-qckx, via a generated EF migration) as dead configuration duplicating the Inventory
/// context's live "expiring soon" horizon (<c>HouseholdInventorySettings.ExpiringSoonDays</c>,
/// already editable at /Settings/Pantry). The view links there instead of shipping a second,
/// inert "expiring soon" knob.
/// </summary>
[Authorize]
public sealed class ExpiryModel(HouseholdExpiryDefaultsService settings) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>True when a POST persisted successfully — drives the confirmation badge.</summary>
    public bool Saved { get; private set; }

    public sealed class InputModel
    {
        [Display(Name = "Default due-days after freezing")]
        [Range(
            HouseholdExpiryDefaultsService.MinDays,
            HouseholdExpiryDefaultsService.MaxDays,
            ErrorMessage = "Choose between {1} and {2} days.")]
        public int AfterFreezingDays { get; set; } = HouseholdExpiryDefaultsService.DefaultAfterFreezing;

        [Display(Name = "Default due-days after thawing")]
        [Range(
            HouseholdExpiryDefaultsService.MinDays,
            HouseholdExpiryDefaultsService.MaxDays,
            ErrorMessage = "Choose between {1} and {2} days.")]
        public int AfterThawingDays { get; set; } = HouseholdExpiryDefaultsService.DefaultAfterThawing;
    }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
            return Page();

        var freezingResult = await settings.SetAfterFreezingAsync(Input.AfterFreezingDays, ct);
        if (freezingResult.IsFailure)
        {
            ModelState.AddModelError(
                nameof(Input) + "." + nameof(InputModel.AfterFreezingDays), freezingResult.Error.Description);
        }

        var thawingResult = await settings.SetAfterThawingAsync(Input.AfterThawingDays, ct);
        if (thawingResult.IsFailure)
        {
            ModelState.AddModelError(
                nameof(Input) + "." + nameof(InputModel.AfterThawingDays), thawingResult.Error.Description);
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        // Reflect the persisted values (in case of any normalization) and confirm.
        await LoadAsync(ct);
        Saved = true;
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var (afterFreezing, afterThawing) = await settings.GetAsync(ct);
        Input.AfterFreezingDays = afterFreezing;
        Input.AfterThawingDays = afterThawing;
    }
}
