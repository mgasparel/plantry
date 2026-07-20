using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Application;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// /Settings/Currency — the household's display currency (plantry-2x6e.1): the ISO 4217 code freshly
/// written money (budgets today) adopts and the presentation edge labels bare-decimal money with.
///
/// GET loads the current value (defaulting to USD when unset). POST persists via
/// <see cref="DisplayCurrencyService"/> and re-renders with a saved badge. Plain server-rendered form
/// (no JS island) per the hypermedia-default UI convention (ADR-020).
///
/// The picker is a curated list of 2-minor-unit currencies. Zero-decimal currencies (e.g. JPY) are the
/// deliberate extension point — supporting them needs per-currency minor-unit handling in
/// <see cref="Plantry.SharedKernel.Money"/> and the yet-to-land MoneyDisplay helper (plantry-2x6e.2),
/// so they are out of scope here. A submitted code outside the list is rejected as invalid.
/// </summary>
[Authorize]
public sealed class CurrencyModel(DisplayCurrencyService settings) : PageModel
{
    /// <summary>Curated 2-minor-unit currencies offered in the picker (label → ISO code).</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> Options =
    [
        ("USD", "USD — US Dollar"),
        ("CAD", "CAD — Canadian Dollar"),
        ("EUR", "EUR — Euro"),
        ("GBP", "GBP — British Pound"),
        ("AUD", "AUD — Australian Dollar"),
        ("NZD", "NZD — New Zealand Dollar"),
    ];

    /// <summary>Bound ISO 4217 code — the select posts one of the curated codes to this property.</summary>
    [BindProperty]
    public string Currency { get; set; } = DisplayCurrencyService.Default;

    /// <summary>True when a POST persisted successfully — drives the confirmation badge.</summary>
    public bool Saved { get; private set; }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        Currency = await settings.GetAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        // Constrain to the curated list — the domain accepts any 3-letter code, but the UI must not.
        if (!Options.Any(o => o.Code == Currency))
        {
            ModelState.AddModelError(nameof(Currency), "Choose a currency from the list.");
            Currency = await settings.GetAsync(ct);
            return Page();
        }

        var result = await settings.SetAsync(Currency, ct);
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return Page();
        }

        // Reflect the persisted value and confirm.
        Currency = await settings.GetAsync(ct);
        Saved = true;
        return Page();
    }
}
