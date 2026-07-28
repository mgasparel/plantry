using System.Text.RegularExpressions;

namespace Plantry.Tests.Architecture;

/// <summary>
/// Source-scanning guard (plantry-lgbu): domain and application code must never read the wall clock
/// ambiently — every timestamp comes from an injected <c>IClock</c> (<c>Plantry.SharedKernel.Domain.IClock</c>,
/// read as <c>clock.UtcNow</c>), never a direct <c>DateTime.UtcNow</c> / <c>DateTime.Now</c> /
/// <c>DateTimeOffset.UtcNow</c> / <c>DateTimeOffset.Now</c> call. An ambient read is untestable (no fixed-clock
/// fixture can control it) and has repeatedly produced real bugs — most recently a UTC-vs-server-local "today"
/// miscalculation in <c>BrowseRecipesQuery</c> (plantry-lgbu) that showed stock as expired, and recipes as not
/// cookable, a day early on any server west of UTC.
///
/// <para>This test reads the C# source of the domain/application bounded contexts — Catalog, Deals,
/// Housekeeping, Identity, Intake, Inventory, MealPlanning, Pricing, Recipes, Shopping, SharedKernel — and fails
/// if any forbidden ambient-clock shape reappears. This is the same domain-purity boundary
/// <see cref="BoundaryTests"/> already enforces (deliberately excluding the <c>*.Infrastructure</c> projects,
/// <c>Plantry.Web</c>, <c>Plantry.Composition</c>, <c>Plantry.Migration.Grocy</c>, <c>Plantry.AppHost</c>,
/// <c>Plantry.Migrator</c>, and <c>Plantry.ServiceDefaults</c> — widening reach to those is a separate,
/// unadjudicated decision, not this guard's).</para>
///
/// <para><b>Precision is the point.</b> The forbidden shapes are the four <i>type-qualified</i> forms only —
/// <c>DateTime.UtcNow</c>, <c>DateTime.Now</c>, <c>DateTimeOffset.UtcNow</c>, <c>DateTimeOffset.Now</c> — never
/// the sanctioned <c>clock.UtcNow</c> idiom (or <c>_clock.UtcNow</c>, or any member access off an
/// <c>IClock</c> instance) that appears throughout the code being guarded. A guard that fires on
/// <c>clock.UtcNow</c> would be worse than no guard at all, so
/// <see cref="Negative_BenignPatterns_AreNotFlagged"/> pins that boundary as hard as
/// <see cref="Positive_ForbiddenPatterns_AreDetected"/> pins reach.</para>
///
/// <para>The tree scan and both theories all delegate to the single <see cref="IsOffendingLine"/> predicate, so
/// reach and precision can never drift apart — the pattern mirrors <c>MoneyFormattingGuardTests</c> in
/// <c>Plantry.Tests.Web</c>. NetArchTest (used elsewhere in this project) expresses type/dependency rules and
/// cannot see method-body IL calls, so a source scan is the right mechanism for this, even though it is the
/// first file-scanning test in <c>Plantry.Tests.Architecture</c>.</para>
/// </summary>
public sealed class AmbientClockGuardTests
{
    /// <summary>The domain/application bounded-context projects this guard scans (the same boundary
    /// <see cref="BoundaryTests"/> enforces). Deliberately excludes every <c>*.Infrastructure</c> project,
    /// <c>Plantry.Web</c>, <c>Plantry.Composition</c>, <c>Plantry.Migration.Grocy</c>, <c>Plantry.AppHost</c>,
    /// <c>Plantry.Migrator</c>, and <c>Plantry.ServiceDefaults</c>.</summary>
    private static readonly string[] ScannedProjects =
    [
        "Plantry.Catalog",
        "Plantry.Deals",
        "Plantry.Housekeeping",
        "Plantry.Identity",
        "Plantry.Intake",
        "Plantry.Inventory",
        "Plantry.MealPlanning",
        "Plantry.Pricing",
        "Plantry.Recipes",
        "Plantry.Shopping",
        "Plantry.SharedKernel",
    ];

    // The four forbidden, type-qualified ambient-clock shapes. Each requires the literal type name
    // immediately followed by ".UtcNow" / ".Now" — "clock.UtcNow" and "_clock.UtcNow" contain no
    // "DateTime"/"DateTimeOffset" substring at all, so they can never match. A word boundary before the type
    // name additionally guards against a hypothetical longer identifier ending in "...DateTime" being misread
    // as the BCL type. Requiring the dot also keeps this from matching a property DECLARATION shaped like
    // "DateTimeOffset UtcNow { get; }" (space, not dot, between the type and the member name — AC9).
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"\bDateTime\.UtcNow\b", RegexOptions.Compiled),
        new(@"\bDateTime\.Now\b", RegexOptions.Compiled),
        new(@"\bDateTimeOffset\.UtcNow\b", RegexOptions.Compiled),
        new(@"\bDateTimeOffset\.Now\b", RegexOptions.Compiled),
    ];

    /// <summary>
    /// The single predicate every caller shares: true when <paramref name="line"/> contains any forbidden
    /// ambient-clock shape. The tree scan and both theories delegate here so reach and precision are pinned to
    /// the exact patterns that ship.
    /// </summary>
    private static bool IsOffendingLine(string line) =>
        ForbiddenPatterns.Any(p => p.IsMatch(line));

    [Fact(DisplayName = "No ambient DateTime.UtcNow/Now or DateTimeOffset.UtcNow/Now outside IClock's SystemClock")]
    public void DomainAndApplicationLayers_HaveNoAmbientClockReads()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();

        foreach (var project in ScannedProjects)
        {
            var projectRoot = Path.Combine(repoRoot, "src", project);
            if (!Directory.Exists(projectRoot))
                throw new InvalidOperationException(
                    $"Expected scanned project directory not found: {projectRoot}. " +
                    "Update AmbientClockGuardTests.ScannedProjects if the project was renamed or removed.");

            foreach (var file in EnumerateSourceFiles(projectRoot))
            {
                // The sole sanctioned ambient read: SystemClock's own implementation of IClock.UtcNow, which
                // legitimately reads the real wall clock so every other caller doesn't have to (plantry-lgbu).
                if (IsSystemClockFile(file))
                    continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (IsOffendingLine(lines[i]))
                        offenders.Add($"{file}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Domain/application code must read the clock through an injected IClock, never ambiently. " +
            "Offending lines:\n" + string.Join("\n", offenders));
    }

    // Every forbidden shape the guard exists to catch — both BCL types, both Now/UtcNow members. If a refactor
    // weakens a pattern, one of these fails loudly.
    [Theory(DisplayName = "Guard flags every forbidden ambient-clock shape")]
    [InlineData(@"public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;")] // the exact plantry-lgbu bug
    [InlineData(@"var today = DateOnly.FromDateTime(DateTime.UtcNow);")]                // the exact BrowseRecipesQuery bug
    [InlineData(@"var stamp = DateTime.Now;")]
    [InlineData(@"var stamp = DateTimeOffset.Now;")]
    [InlineData(@"if (session?.CreatedAt is null) return DateTimeOffset.UtcNow;")]
    public void Positive_ForbiddenPatterns_AreDetected(string line) =>
        Assert.True(IsOffendingLine(line), $"Guard should have flagged: {line}");

    // The sanctioned idiom and adjacent-but-benign shapes that must NEVER be flagged. This is the precision half
    // of the guard: a false positive on the codebase's own sanctioned pattern is worse than no guard at all.
    [Theory(DisplayName = "Guard does not flag the sanctioned clock.UtcNow idiom or benign look-alikes")]
    [InlineData(@"var now = clock.UtcNow;")]                                     // the sanctioned idiom
    [InlineData(@"var now = _clock.UtcNow;")]                                    // field-backed variant
    [InlineData(@"var today = DateOnly.FromDateTime(clock.UtcNow.LocalDateTime);")] // the plantry-lgbu FIX
    [InlineData(@"var today = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());")] // equivalent local-conversion idiom
    [InlineData(@"public interface IClock { DateTimeOffset UtcNow { get; } }")]     // property DECLARATION, not a read
    [InlineData(@"DateTimeOffset UtcNow { get; }")]                                 // same, on its own line
    // A comment mentioning the sanctioned fix's exact shape — this line is real, verbatim, from
    // src/Plantry.Intake.Infrastructure/ImportSessionRepository.cs:60 (an Infrastructure file this guard does
    // not scan anyway, but the predicate itself must not flag prose about the fix either).
    [InlineData(@"// Mirrors the DateOnly.FromDateTime(clock.UtcNow.ToLocalTime()) idiom used elsewhere.")]
    public void Negative_BenignPatterns_AreNotFlagged(string line) =>
        Assert.False(IsOffendingLine(line), $"Guard should NOT have flagged: {line}");

    /// <summary>True for <c>src/Plantry.SharedKernel/Domain/IClock.cs</c> — the sole exemption, matched by file
    /// path (mirrors how <c>MoneyFormattingGuardTests</c> exempts <c>MoneyDisplay.cs</c>). <c>SystemClock</c>
    /// lives in this same file and is where the ambient read legitimately happens.</summary>
    private static bool IsSystemClockFile(string file) =>
        Path.GetFileName(file).Equals("IClock.cs", StringComparison.OrdinalIgnoreCase)
        && Path.GetFullPath(file).Replace('\\', '/').Contains("/Plantry.SharedKernel/Domain/IClock.cs");

    private static IEnumerable<string> EnumerateSourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            // obj/ holds generated build artifacts; Migrations/ holds EF-generated snapshots — neither is
            // hand-authored domain/application code this guard is meant to police.
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(AmbientClockGuardTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Plantry.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (Plantry.sln).");
    }
}
