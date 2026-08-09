using System.Text.RegularExpressions;

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
