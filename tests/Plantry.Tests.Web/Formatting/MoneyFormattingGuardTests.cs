using System.Text.RegularExpressions;

namespace Plantry.Tests.Web.Formatting;

/// <summary>
/// Source-scanning guard (plantry-2x6e.2, hardened in plantry-2i71): every server-rendered money value must go
/// through <see cref="Plantry.Web.MoneyDisplay"/>, never an ad-hoc culture-dependent currency specifier or a
/// hardcoded <c>"$"</c> / <c>"~$"</c> prefix concatenated with a formatted number. This test reads the
/// <c>Plantry.Web</c> source (C# + Razor, excluding <c>wwwroot</c> and MoneyDisplay itself) and fails if any
/// forbidden pattern reappears, so a future call site cannot silently reintroduce the culture-dependent <c>¤</c>
/// glyph bug or a currency that ignores the household display currency.
///
/// The guard forbids three shapes of the .NET currency specifier, all culture-dependent:
///   1. Direct <c>ToString("C…")</c> / <c>ToString("c…")</c> (upper- and lower-case).
///   2. The composite / interpolated form — <c>String.Format("{0:C2}", x)</c>, <c>$"{amount:C2}"</c>,
///      <c>{0:c}</c> — where the specifier lives inside a <c>{…:C…}</c> format item.
///   3. A hardcoded <c>"$"</c> / <c>"~$"</c> string literal used to prefix a formatted amount.
/// The <see cref="Positive_ForbiddenPatterns_AreDetected"/> and <see cref="Negative_BenignPatterns_AreNotFlagged"/>
/// theories exercise the same <see cref="IsOffendingLine"/> predicate the tree scan uses, pinning both its reach
/// (all three shapes, both cases) and its precision (no false positives on Alpine object literals, attribute
/// bindings, ternaries, or non-currency format specifiers).
/// </summary>
public sealed class MoneyFormattingGuardTests
{
    // (1) Direct ".NET currency specifier" — ToString("C"), ToString("C2"), and the lowercase ToString("c…")
    // — is culture-dependent and produced the '¤' glyph bug. The letter is matched case-insensitively ([Cc])
    // because "c" and "C" are the same standard numeric format specifier. Forbidden outside MoneyDisplay.
    private static readonly Regex CurrencySpecifier = new(@"ToString\(""[Cc]", RegexOptions.Compiled);

    // (2) The composite / interpolated currency specifier: a "{…:C…}" format item, e.g. String.Format("{0:C2}", x),
    // $"{amount:C2}", or a bare "{0:c}". Requiring the whole braced item ({, then any non-brace run for the
    // index/alignment, then ":", the C/c letter, optional precision digits, and the closing }) is what keeps this
    // from firing on benign minified object literals like {mode:card} or attribute bindings like :class="{…}" —
    // there the colon-C is never immediately followed by (optional digits then) a closing brace.
    private static readonly Regex CompositeCurrencySpecifier = new(@"\{[^{}]*:[Cc]\d*\}", RegexOptions.Compiled);

    // (3) A hardcoded dollar prefix as a string literal ("$" or "~$"), the ad-hoc idiom the removed call sites used
    // to concatenate/interpolate a formatted amount. A literal '$' inside markup (e.g. a <span>$</span> input
    // affordance) is NOT a quoted string literal and so does not match.
    private static readonly Regex DollarPrefixLiteral = new(@"""~?\$""", RegexOptions.Compiled);

    /// <summary>
    /// The single predicate all callers share: true when <paramref name="line"/> contains any forbidden money
    /// formatting shape. The tree scan and both theories delegate here so reach and precision are pinned to the
    /// exact patterns that ship.
    /// </summary>
    private static bool IsOffendingLine(string line) =>
        CurrencySpecifier.IsMatch(line)
        || CompositeCurrencySpecifier.IsMatch(line)
        || DollarPrefixLiteral.IsMatch(line);

    [Fact(DisplayName = "No ad-hoc currency-format specifier or hardcoded $-prefix money outside MoneyDisplay")]
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
                if (IsOffendingLine(lines[i]))
                    offenders.Add($"{file}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Server-rendered money must go through MoneyDisplay. Offending lines:\n" + string.Join("\n", offenders));
    }

    // Every forbidden shape the guard exists to catch — direct (both cases), composite/interpolated (both cases),
    // and the hardcoded $-prefix literal. If a refactor weakens a pattern, one of these fails loudly.
    [Theory(DisplayName = "Guard flags every forbidden currency-formatting shape")]
    [InlineData(@"var s = amount.ToString(""C"");")]            // direct, upper-case, no precision
    [InlineData(@"var s = amount.ToString(""C2"");")]           // direct, upper-case, with precision
    [InlineData(@"var s = amount.ToString(""c"");")]            // direct, lower-case, no precision
    [InlineData(@"var s = amount.ToString(""c2"");")]           // direct, lower-case, with precision
    [InlineData(@"var s = string.Format(""{0:C2}"", amount);")] // composite, String.Format
    [InlineData(@"var s = $""{amount:C2}"";")]                  // interpolated, upper-case
    [InlineData(@"var s = $""{amount:c}"";")]                   // interpolated, lower-case, no precision
    [InlineData(@"<span>@($""{total:C2}"")</span>")]            // interpolated inside Razor markup
    [InlineData(@"var s = $""{item.Price:C2} each"";")]         // interpolated with member-access expression
    [InlineData(@"var label = ""$"" + amount;")]               // hardcoded $-prefix literal
    [InlineData(@"var label = ""~$"" + amount;")]              // hardcoded approximate $-prefix literal
    public void Positive_ForbiddenPatterns_AreDetected(string line) =>
        Assert.True(IsOffendingLine(line), $"Guard should have flagged: {line}");

    // Benign lines that superficially resemble the forbidden shapes — colons before C/c, braces, dollar signs in
    // markup, non-currency format specifiers — and must NOT be flagged. These pin precision so the hardened
    // composite pattern never fires on Alpine object literals, attribute bindings, ternaries, or CSS.
    [Theory(DisplayName = "Guard does not flag benign currency-adjacent patterns")]
    [InlineData(@"var s = count.ToString(""N2"");")]                     // non-currency numeric specifier
    [InlineData(@"var s = date.ToString(""yyyy-MM-dd"");")]              // date format specifier
    [InlineData(@"var s = string.Format(""{0:N2}"", count);")]          // composite, non-currency
    [InlineData(@"var s = $""{count:D3}"";")]                           // interpolated, non-currency
    [InlineData(@"<div x-data=""{ mode: 'card', open: false }""></div>")] // Alpine object literal, spaced
    [InlineData(@"<div x-data=""{active:true,mode:'card'}""></div>")]     // Alpine object literal, minified
    [InlineData(@"<div :class=""{ 'is-active': isActive }""></div>")]     // Alpine class binding (":class", colon-c)
    [InlineData(@"var label = isActive ? ""on"" : ""off"";")]           // ternary, colon before quoted "off"
    [InlineData(@"<span>$</span>")]                                     // literal '$' in markup, not a string literal
    [InlineData(@"<style>.tag { color: crimson }</style>")]             // CSS declaration, colon-space-c
    [InlineData(@"<a href=""/catalog/{id:int}"">link</a>")]            // route constraint, colon-i
    [InlineData(@"@Html.DisplayFor(m => m.Total)")]                     // sanctioned display path, no specifier
    public void Negative_BenignPatterns_AreNotFlagged(string line) =>
        Assert.False(IsOffendingLine(line), $"Guard should NOT have flagged: {line}");

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
