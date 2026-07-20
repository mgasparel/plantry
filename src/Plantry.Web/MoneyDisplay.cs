using Plantry.SharedKernel;

namespace Plantry.Web;

/// <summary>
/// The single culture-free formatter for every server-rendered money value (plantry-2x6e.2). It replaces the
/// ad-hoc <c>ToString("C2")</c> / <c>"$" + ToString("F2")</c> call sites scattered across the web layer.
/// </summary>
/// <remarks>
/// <para><b>No <see cref="System.Globalization.CultureInfo"/> anywhere.</b> The amount is rendered from integer
/// minor units and the symbol comes from a deterministic ISO-4217 → symbol map, so the output never depends on
/// the ambient thread/host culture. This kills the <c>¤</c> currency-glyph bug class at the root (superseding
/// plantry-xtmt) rather than papering over it by pinning a display culture at startup.</para>
/// <para><b>Symbol map.</b> USD/CAD/AUD/NZD → <c>$</c>, EUR → <c>€</c>, GBP → <c>£</c> — the currencies the
/// Settings picker offers (plantry-2x6e.1). An unmapped code falls back to <c>"CODE 12.34"</c> (upper-cased
/// code + space + amount), so a currency outside the curated list is still legible and never masquerades as
/// dollars. Every rendering is exactly 2 decimal places, a <c>.</c> decimal separator, and no thousands
/// grouping — matching what every prior call site produced.</para>
/// </remarks>
public static class MoneyDisplay
{
    // ISO 4217 (upper-case) → display symbol. Case-insensitive so a lower-case code still resolves; the domain
    // stores codes upper-cased (see Money / Household.SetDisplayCurrency), this is defence in depth.
    private static readonly IReadOnlyDictionary<string, string> Symbols =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = "$",
            ["CAD"] = "$",
            ["AUD"] = "$",
            ["NZD"] = "$",
            ["EUR"] = "€",
            ["GBP"] = "£",
        };

    /// <summary>
    /// The display symbol for a currency: the mapped glyph (<c>$</c>/<c>€</c>/<c>£</c>) for a curated code, or
    /// the upper-cased code itself as the fallback "symbol" for anything unmapped. Exposed so the client-side
    /// money islands (plantry-2x6e.3) draw their prefix from this one map rather than re-deriving it.
    /// </summary>
    public static string Symbol(string currency) =>
        Symbols.TryGetValue(currency ?? string.Empty, out var symbol) ? symbol : Normalize(currency);

    /// <summary>Formats a bare decimal amount with the given ISO-4217 currency (see class remarks).</summary>
    public static string Format(decimal amount, string currency)
    {
        var body = FormatAmount(amount);
        return Symbols.TryGetValue(currency ?? string.Empty, out var symbol)
            ? symbol + body
            : $"{Normalize(currency)} {body}";
    }

    /// <summary>
    /// Formats a <see cref="Money"/> with <b>its own</b> currency — never the household display currency, so a
    /// stored value (e.g. a weekly budget) is never silently relabelled when the household switches currency.
    /// </summary>
    public static string Format(Money money) => Format(money.ToDecimal(), money.Currency);

    /// <summary>
    /// Renders a decimal to a fixed <c>0.00</c> shape with a <c>.</c> separator and no grouping, built from
    /// integer minor units so no <see cref="System.Globalization.CultureInfo"/> is consulted. Integer
    /// <c>ToString</c> ("G") never inserts group separators and always emits Latin digits, so the result is
    /// identical under every ambient culture (including the container's invariant/C locale).
    /// </summary>
    private static string FormatAmount(decimal amount)
    {
        var negative = amount < 0m;
        var cents = (long)Math.Round(Math.Abs(amount) * 100m, MidpointRounding.AwayFromZero);
        var whole = cents / 100;
        var frac = cents % 100;
        return $"{(negative ? "-" : string.Empty)}{whole}.{frac / 10}{frac % 10}";
    }

    private static string Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "XXX" : currency.Trim().ToUpperInvariant();
}
