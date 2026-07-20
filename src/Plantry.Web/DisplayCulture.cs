using System.Globalization;

namespace Plantry.Web;

/// <summary>
/// Resolves the application's display culture — the culture used to format money and other
/// culture-sensitive display values (recipe cost-per-serving, deal prices) via <c>ToString("C2")</c>.
/// </summary>
/// <remarks>
/// The runtime container (<c>mcr.microsoft.com/dotnet/aspnet</c>) inherits the base image's C/POSIX
/// locale, which .NET maps to the <b>invariant</b> culture — whose currency symbol is the generic
/// placeholder <c>¤</c> (so cost renders as <c>¤0.00</c> instead of <c>$0.00</c>). Pinning an explicit
/// culture at startup (see <c>Program.cs</c>) makes money formatting independent of the host/container
/// locale (plantry-xtmt). Sourced from config with an <c>en-US</c> default so an operator can override
/// without a rebuild; Plantry is single-currency (USD) today. Extracted as a helper so the resolution
/// is unit-testable without booting the host.
/// </remarks>
public static class DisplayCulture
{
    /// <summary>Config key an operator can set to override the display culture (e.g. "en-CA").</summary>
    public const string ConfigKey = "Localization:DefaultCulture";

    /// <summary>Default display culture when config does not specify one.</summary>
    public const string DefaultCultureName = "en-US";

    /// <summary>
    /// Resolves the configured display culture, falling back to <see cref="DefaultCultureName"/> when
    /// the config value is absent or blank. The result never depends on the ambient OS/thread culture.
    /// </summary>
    public static CultureInfo Resolve(IConfiguration configuration)
    {
        var name = configuration[ConfigKey];
        return CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(name) ? DefaultCultureName : name);
    }
}
