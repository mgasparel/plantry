using System.Globalization;
using Plantry.SharedKernel;
using Plantry.Web;

namespace Plantry.Tests.Web.Formatting;

/// <summary>
/// Unit tests for <see cref="MoneyDisplay"/> (plantry-2x6e.2) — the single culture-free money formatter that
/// supersedes the scattered <c>ToString("C2")</c> / <c>"$" + F2</c> call sites and the plantry-xtmt culture
/// pin. Proves the deterministic symbol map, the unmapped-code fallback, the <see cref="Money"/> overload's
/// own-currency behaviour, 2-decimal rounding, and — the load-bearing property — that the output never depends
/// on the ambient thread culture (the root of the <c>¤</c> glyph bug).
/// </summary>
public sealed class MoneyDisplayTests
{
    [Theory(DisplayName = "Symbol — every curated code maps to its glyph")]
    [InlineData("USD", "$")]
    [InlineData("CAD", "$")]
    [InlineData("AUD", "$")]
    [InlineData("NZD", "$")]
    [InlineData("EUR", "€")]
    [InlineData("GBP", "£")]
    public void Symbol_CuratedCode_MapsToGlyph(string code, string expected) =>
        Assert.Equal(expected, MoneyDisplay.Symbol(code));

    [Fact(DisplayName = "Symbol — is case-insensitive")]
    public void Symbol_CaseInsensitive() => Assert.Equal("€", MoneyDisplay.Symbol("eur"));

    [Fact(DisplayName = "Symbol — an unmapped code falls back to the upper-cased code itself")]
    public void Symbol_Unmapped_FallsBackToCode() => Assert.Equal("JPY", MoneyDisplay.Symbol("jpy"));

    [Theory(DisplayName = "Format(decimal) — mapped code renders symbol + 2dp amount, no grouping")]
    [InlineData(4.99, "USD", "$4.99")]
    [InlineData(0, "USD", "$0.00")]
    [InlineData(9.5, "EUR", "€9.50")]
    [InlineData(3, "GBP", "£3.00")]
    [InlineData(1234.5, "CAD", "$1234.50")]
    public void Format_MappedCode_SymbolThenAmount(decimal amount, string currency, string expected) =>
        Assert.Equal(expected, MoneyDisplay.Format(amount, currency));

    [Fact(DisplayName = "Format(decimal) — an unmapped code falls back to 'CODE 12.34'")]
    public void Format_UnmappedCode_CodeSpaceAmount() =>
        Assert.Equal("JPY 12.34", MoneyDisplay.Format(12.34m, "jpy"));

    [Theory(DisplayName = "Format(decimal) — rounds to 2 places away from zero")]
    [InlineData(1.005, "$1.01")]
    [InlineData(1.004, "$1.00")]
    [InlineData(2.675, "$2.68")]
    public void Format_RoundsTwoDecimalsAwayFromZero(decimal amount, string expected) =>
        Assert.Equal(expected, MoneyDisplay.Format(amount, "USD"));

    [Fact(DisplayName = "Format(Money) — uses the Money's OWN currency, not a passed one")]
    public void Format_Money_UsesOwnCurrency() =>
        Assert.Equal("£12.50", MoneyDisplay.Format(Money.FromDecimal(12.50m, "GBP")));

    [Fact(DisplayName = "Format — output is culture-free even under a comma-decimal ambient culture")]
    public void Format_IsCultureFree()
    {
        // de-DE uses ',' as the decimal separator and '.' as the group separator, and its invariant/C-locale
        // cousin renders the currency symbol as '¤'. A correct formatter ignores all of that.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("$1234.50", MoneyDisplay.Format(1234.5m, "USD"));
            Assert.Equal("€0.00", MoneyDisplay.Format(0m, "EUR"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
