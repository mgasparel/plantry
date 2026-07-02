using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Plantry.Deals.Domain;

/// <summary>
/// Pure domain service (DL-O6): turns a raw advertised item name into a deterministic
/// <see cref="NormalizedName"/> — lowercase, trim/collapse whitespace, strip pack-size/unit tokens,
/// strip punctuation. <b>No I/O, no AI</b>, so the <see cref="DealMatchMemory"/> key and the dedup
/// behaviour are stable and reproducible across pulls (DD4). Determinism holds <i>per
/// <see cref="NormalizerVersion"/></i>: the stripping rules will be tuned against real flyer data, and
/// each change re-keys some inputs — so the version is stamped and a bump triggers a one-time backfill.
/// </summary>
public static partial class DealNormalizer
{
    /// <summary>
    /// The version of the normalization rules below. <b>Bump this whenever the stripping rules change</b>
    /// so <see cref="DealMatchMemory"/> rows can be flagged for a one-time re-normalization backfill (DD4).
    /// </summary>
    public const int NormalizerVersion = 1;

    // Pack-size / unit tokens: a number (optionally decimal), an optional multi-buy factor ("12x355ml"),
    // optionally spaced before a unit (g, kg, mg, ml, l, oz, lb, lbs, ct, pk, pkt, pack).
    [GeneratedRegex(
        @"\b\d+(?:\.\d+)?(?:\s?x\s?\d+(?:\.\d+)?)?\s?(?:kg|g|mg|ml|l|oz|lbs|lb|ct|pk|pkt|pack)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex PackSizeToken();

    // Bare multi-buy prefixes with no trailing unit, e.g. "2x" in "2x Cola".
    [GeneratedRegex(@"\b\d+\s?x\b", RegexOptions.CultureInvariant)]
    private static partial Regex MultiBuyToken();

    // Any run of characters that is not a letter, digit, or single space becomes a space.
    [GeneratedRegex(@"[^a-z0-9 ]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// Normalizes <paramref name="rawName"/> deterministically. A null/blank input yields an empty
    /// normalized value (still stamped with the current version).
    /// </summary>
    public static NormalizedName Normalize(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return new NormalizedName(string.Empty, NormalizerVersion);

        // 1. Lowercase (invariant) and strip diacritics so "Crème" and "Creme" key identically.
        var value = StripDiacritics(rawName.ToLowerInvariant());

        // 2. Remove pack-size / unit tokens (e.g. "500g", "12x355ml", "12ct") then bare multi-buy prefixes.
        value = PackSizeToken().Replace(value, " ");
        value = MultiBuyToken().Replace(value, " ");

        // 3. Replace all remaining punctuation/symbols with a space.
        value = NonAlphanumeric().Replace(value, " ");

        // 4. Collapse whitespace and trim.
        value = WhitespaceRun().Replace(value, " ").Trim();

        return new NormalizedName(value, NormalizerVersion);
    }

    private static string StripDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
