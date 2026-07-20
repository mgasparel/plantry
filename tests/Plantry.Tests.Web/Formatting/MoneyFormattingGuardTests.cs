using System.Text.RegularExpressions;

namespace Plantry.Tests.Web.Formatting;

/// <summary>
/// Source-scanning guard (plantry-2x6e.2): every server-rendered money value must go through
/// <see cref="Plantry.Web.MoneyDisplay"/>, never an ad-hoc <c>ToString("C…")</c> currency specifier or a
/// hardcoded <c>"$"</c> / <c>"~$"</c> prefix concatenated with a formatted number. This test reads the
/// <c>Plantry.Web</c> source (C# + Razor, excluding <c>wwwroot</c> and MoneyDisplay itself) and fails if either
/// pattern reappears, so a future call site cannot silently reintroduce the culture-dependent <c>¤</c> bug or a
/// currency that ignores the household display currency.
/// </summary>
public sealed class MoneyFormattingGuardTests
{
    // The ".NET currency specifier" — ToString("C"), ToString("C2", …) etc. — is culture-dependent and produced
    // the '¤' glyph bug. Forbidden outside MoneyDisplay.
    private static readonly Regex CurrencySpecifier = new(@"ToString\(""C", RegexOptions.Compiled);

    // A hardcoded dollar prefix as a string literal ("$" or "~$"), the ad-hoc idiom the removed call sites used
    // to concatenate/interpolate a formatted amount. A literal '$' inside markup (e.g. a <span>$</span> input
    // affordance) is NOT a quoted string literal and so does not match.
    private static readonly Regex DollarPrefixLiteral = new(@"""~?\$""", RegexOptions.Compiled);

    [Fact(DisplayName = "No currency-format ToString(\"C\") or hardcoded $-prefix money outside MoneyDisplay")]
    public void PlantryWeb_HasNoAdHocMoneyFormatting()
    {
        var webRoot = Path.Combine(RepoRoot(), "src", "Plantry.Web");

        var offenders = new List<string>();
        foreach (var file in EnumerateSourceFiles(webRoot))
        {
            // MoneyDisplay itself is the one sanctioned home of the '$' symbol map and mentions ToString("C") in
            // its doc comment describing what it replaces.
            if (Path.GetFileName(file).Equals("MoneyDisplay.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (CurrencySpecifier.IsMatch(lines[i]) || DollarPrefixLiteral.IsMatch(lines[i]))
                    offenders.Add($"{file}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Server-rendered money must go through MoneyDisplay. Offending lines:\n" + string.Join("\n", offenders));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            // wwwroot holds vendored JS/CSS and the client-side islands (their money formatting is a separate
            // concern, plantry-2x6e.3); obj holds generated build artifacts.
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(MoneyFormattingGuardTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Plantry.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (Plantry.sln).");
    }
}
