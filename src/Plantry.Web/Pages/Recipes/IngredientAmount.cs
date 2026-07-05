using System.Globalization;

namespace Plantry.Web.Pages.Recipes;

/// <summary>
/// Canonical rendering for ingredient amounts (bead plantry-jun6).
///
/// One rule, one home. Every recipe render site — the Details ingredient rows, the Cook
/// shortfall/variant tags, and the editor's read-only row summary — routes its amount through
/// <see cref="Format(decimal)"/> instead of hand-rolling <c>G29</c>/<c>G4</c>/<c>ToString()</c>.
/// The JS twin (<c>wwwroot/js/ingredient-amount.js</c>) mirrors this exact rule so a
/// server-rendered amount and a client-rendered (servings-scaled) amount always agree.
///
/// v1 rule: round to at most <see cref="MaxDecimals"/> fractional digits, then strip trailing
/// zeros / a bare trailing decimal point, invariant culture. Rounding tames the noisy tails a
/// scaled or unit-converted amount can produce (e.g. 100 ÷ 3); for an exact stored quantity with
/// no more than <see cref="MaxDecimals"/> decimals the value passes through untouched apart from
/// trailing-zero cleanup.
///
/// Deliberately isolated so future expansion the author has in mind (fractions, unit-aware
/// rendering) has a single seam to grow from.
/// </summary>
public static class IngredientAmount
{
    /// <summary>Maximum fractional digits retained before trailing zeros are stripped.</summary>
    public const int MaxDecimals = 4;

    // "0.####" strips trailing zeros and the decimal point when the value is whole. Kept in lock-step
    // with MaxDecimals (four '#').
    private const string Pattern = "0.####";

    /// <summary>
    /// Render <paramref name="value"/> as a clean amount string: no trailing zeros, no dangling
    /// decimal point. "500.000" → "500", "1.50" → "1.5", "1" → "1".
    /// </summary>
    public static string Format(decimal value)
    {
        var rounded = Math.Round(value, MaxDecimals, MidpointRounding.AwayFromZero);
        return rounded.ToString(Pattern, CultureInfo.InvariantCulture);
    }
}
