using System.Globalization;
using System.Text.RegularExpressions;
using Plantry.Market.Application;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// View-level display helpers for the deal review queue (P5-8 / DJ4). These are <b>presentation only</b> —
/// they never touch the deal aggregate or persist anything. The raw flyer string stays verbatim in the
/// domain and in the ACL quarantine (DD6); these methods only shape the pixels (decision q9zr.10).
/// </summary>
public static partial class DealReviewDisplay
{
    // Boundary preceders that trigger a capital: start-of-string, whitespace, hyphen, em/en dash, slash,
    // open-paren. Deliberately excludes the apostrophe so possessives/plurals stay lowercase after it
    // (FRANK'S → Frank's, 12'S → 12's), per q9zr.10's title-casing rules.
    [GeneratedRegex(@"(^|[\s\-—–/(])(\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex TitleCaseBoundary();

    /// <summary>
    /// Renders an ALL-CAPS flyer name in display title case: lowercase the whole string, then capitalise the
    /// first letter after the start or a space/dash/slash/paren boundary — but never after an apostrophe. The
    /// verbatim raw string is unchanged; callers keep it one hover away in the element's <c>title</c> attribute.
    /// </summary>
    public static string TitleCase(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw ?? string.Empty;

        var lowered = raw.ToLowerInvariant();
        return TitleCaseBoundary().Replace(
            lowered, m => m.Groups[1].Value + char.ToUpperInvariant(m.Groups[2].Value[0]));
    }

    /// <summary>
    /// True for a flyer-noise row: a non-positive price (e.g. an "AD MATCH" line advertised at $0.00). These
    /// carry no usable price, so the queue de-emphasises them and flags them — a view-level judgement only,
    /// no domain state (the deal is still a normal Pending deal the user can reject).
    /// </summary>
    public static bool IsNoise(decimal price) => price <= 0m;

    /// <summary>
    /// Formats the advertised price qualifier. A missing unit leaves the amount unqualified; a quantity of
    /// one uses the compact unit form ("/ each"), while other positive quantities remain explicit.
    /// </summary>
    public static string? FormatPriceBasis(decimal? quantity, string? unitCode)
    {
        if (string.IsNullOrWhiteSpace(unitCode))
            return null;

        var normalizedCode = unitCode.Trim();
        var quantityPrefix = quantity is > 0m && quantity != 1m
            ? $"{quantity.Value.ToString("0.###", CultureInfo.InvariantCulture)} "
            : string.Empty;
        return $"/ {quantityPrefix}{normalizedCode}";
    }

    /// <summary>Converts a normalized base-unit price to the product's displayed inventory unit.</summary>
    public static decimal PriceForUnit(decimal normalizedPrice, decimal? factorToBase) =>
        factorToBase is > 0m ? normalizedPrice * factorToBase.Value : normalizedPrice;

    /// <summary>
    /// Renders a <see cref="DealPurchaseContext.AverageUnitPrice"/> for display (plantry-oh27.6 fix). A leaf
    /// suggestion's average is per-BASE-unit (<see cref="DealPurchaseContext.AverageBasisUnitId"/> null) and
    /// still needs the <see cref="PriceForUnit"/> lift into the displayed inventory unit. A parent
    /// suggestion's average (<see cref="AverageBasisUnitId"/> set) is <b>already</b> expressed per 1 of the
    /// parent's own default unit — the same unit <paramref name="factorToBase"/> would lift into — so
    /// applying <see cref="PriceForUnit"/> again double-converts it (a real 10%-below deal rendering as
    /// ~1000× the correct price). Route every "You pay" render through this one method instead of calling
    /// <see cref="PriceForUnit"/> directly, so the basis check can never be forgotten at a call site.
    /// </summary>
    public static decimal AverageUnitPriceForDisplay(DealPurchaseContext purchase, decimal? factorToBase) =>
        purchase.AverageBasisUnitId is null
            ? PriceForUnit(purchase.AverageUnitPrice, factorToBase)
            : purchase.AverageUnitPrice;

    /// <summary>
    /// Builds the copy for the unit warning. The warning is deliberately gated by unit identity and by both
    /// display codes, so missing reference data never invents a mismatch.
    /// </summary>
    public static string? BuildUnitMismatchHint(DealReviewView view)
    {
        if (!view.HasKnownUnitMismatch
            || string.IsNullOrWhiteSpace(view.DealUnitCode)
            || string.IsNullOrWhiteSpace(view.SuggestedProductUnitCode)
            || string.IsNullOrWhiteSpace(view.SuggestedProductName))
            return null;

        return $"This deal is priced per {view.DealUnitCode.Trim()}. You stock {view.SuggestedProductName} by {view.SuggestedProductUnitCode.Trim()}. Check how many are in a {view.DealUnitCode.Trim()} before comparing prices or confirming the match.";
    }

    /// <summary>Quiet explanation shown when a purchase context exists but its percent comparison is unavailable.</summary>
    public static string BuildPriceComparisonNote(DealReviewView view)
    {
        if (string.IsNullOrWhiteSpace(view.DealUnitCode))
            return "The flyer did not provide a usable unit, so the advertised price remains unqualified.";
        if (string.IsNullOrWhiteSpace(view.SuggestedProductUnitCode))
            return "Price delta is withheld because Plantry cannot identify the inventory unit.";
        if (!string.Equals(view.DealUnitCode.Trim(), view.SuggestedProductUnitCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"Price delta is withheld because Plantry cannot compare a {view.DealUnitCode.Trim()} with {view.SuggestedProductUnitCode.Trim()} without a conversion.";
        return "Price delta is withheld because the advertised price could not be normalized to the inventory unit.";
    }

    /// <summary>
    /// Renders a purchase-cadence <see cref="TimeSpan"/> as the "every ~3 weeks" copy (plantry-gtgl, Deals
    /// review purchase context) — intervals under 10 days read in days, under 10 weeks read in weeks,
    /// otherwise in months (30.44-day average month, matching the "~N months" granularity used elsewhere
    /// the app speaks in rough calendar terms rather than exact days).
    /// </summary>
    public static string FormatPurchaseInterval(TimeSpan interval)
    {
        var days = interval.TotalDays;
        if (days < 10)
            return Plural(Math.Max(1, (int)Math.Round(days)), "day");
        if (days < 70)
            return Plural((int)Math.Round(days / 7), "week");
        return Plural((int)Math.Round(days / 30.44), "month");
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
