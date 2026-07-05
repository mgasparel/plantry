using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Application;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// /Settings/Ai — the household's single "AI assistance" switch (plantry-qll2.1). Governs the
/// assistive/provisional-value AI class (recipe tag suggestions, the diet-tag contradiction nudge,
/// unit-conversion resolution); receipt scanning is deliberately not governed here.
///
/// GET loads the current value (defaulting to ON when unset). POST persists via
/// <see cref="AiAssistanceSettingsService"/> and re-renders with a saved badge. Plain server-rendered
/// form (no JS) per the hypermedia-default UI convention.
/// </summary>
[Authorize]
public sealed class AiModel(AiAssistanceSettingsService settings) : PageModel
{
    /// <summary>Bound On/Off state — the seg-ctrl posts "true"/"false" to this property.</summary>
    [BindProperty]
    public bool Enabled { get; set; } = true;

    /// <summary>True when a POST persisted successfully — drives the confirmation badge.</summary>
    public bool Saved { get; private set; }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        Enabled = await settings.IsEnabledAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        var result = await settings.SetEnabledAsync(Enabled, ct);
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return Page();
        }

        // Reflect the persisted value and confirm.
        Enabled = await settings.IsEnabledAsync(ct);
        Saved = true;
        return Page();
    }
}
