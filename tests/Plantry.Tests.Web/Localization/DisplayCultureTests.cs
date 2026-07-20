using System.Globalization;
using Microsoft.Extensions.Configuration;
using Plantry.Web;

namespace Plantry.Tests.Web.Localization;

/// <summary>
/// Coverage for the money-display culture pin (plantry-xtmt). Proves that recipe cost / deal prices
/// format with a real currency symbol ($) sourced from config, independent of the ambient OS/container
/// culture — the invariant culture (the container's C/POSIX locale) would otherwise render the generic
/// placeholder '¤'.
/// </summary>
public sealed class DisplayCultureTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void Resolve_Defaults_To_EnUs_When_Config_Absent() =>
        Assert.Equal("en-US", DisplayCulture.Resolve(Config()).Name);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_Falls_Back_To_Default_When_Config_Blank(string configured) =>
        Assert.Equal("en-US", DisplayCulture.Resolve(Config((DisplayCulture.ConfigKey, configured))).Name);

    [Fact]
    public void Resolve_Honours_Config_Override() =>
        Assert.Equal("en-CA", DisplayCulture.Resolve(Config((DisplayCulture.ConfigKey, "en-CA"))).Name);

    [Fact]
    public void Resolved_Default_Culture_Formats_Money_With_Dollar_Sign()
    {
        var culture = DisplayCulture.Resolve(Config());

        Assert.Equal("$", culture.NumberFormat.CurrencySymbol);
        Assert.Equal("$0.00", 0m.ToString("C2", culture));
        Assert.Equal("$4.99", 4.99m.ToString("C2", culture));
    }

    /// <summary>
    /// The whole point of the fix: the resolved culture is independent of the ambient thread culture.
    /// Even with the invariant culture ('¤' placeholder) installed as the current culture — exactly the
    /// container's failure mode — the resolved display culture still formats money with '$'.
    /// </summary>
    [Fact]
    public void Resolved_Culture_Formats_Dollar_Even_Under_Invariant_Ambient_Culture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            // Sanity-check the failure mode the fix addresses: invariant renders the generic placeholder.
            Assert.Equal("¤0.00", 0m.ToString("C2"));

            var resolved = DisplayCulture.Resolve(Config());
            Assert.Equal("$0.00", 0m.ToString("C2", resolved));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
