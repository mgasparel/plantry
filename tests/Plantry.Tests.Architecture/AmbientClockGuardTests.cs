using System.Text.RegularExpressions;

namespace Plantry.Tests.Architecture;

/// <summary>
/// Source-scanning guard (plantry-lgbu, hardened plantry-vgze, extended plantry-l639): domain and application
/// code must never read the wall clock — or the machine's own time zone — ambiently. Every instant comes from
/// an injected <c>IClock</c> (<c>Plantry.SharedKernel.Domain.IClock</c>, read as <c>clock.UtcNow</c>), and
/// every server-local conversion goes through <c>Plantry.SharedKernel.Domain.ClockExtensions</c>
/// (<c>clock.LocalNow()</c> / <c>clock.ToLocal(...)</c> / <c>clock.ToLocalDate(...)</c>) rather than a direct
/// <c>DateTime.UtcNow</c> / <c>DateTime.Now</c> / <c>DateTime.Today</c> / <c>DateTimeOffset.UtcNow</c> /
/// <c>DateTimeOffset.Now</c> / <c>TimeProvider.System</c> call, or a <c>.LocalDateTime</c> / <c>.ToLocalTime()</c>
/// read (which silently converts via the machine's own <c>TimeZoneInfo.Local</c>, bypassing the injected
/// clock's zone entirely). An
/// ambient read is untestable (no fixed-clock fixture can control it) and has repeatedly produced real bugs —
/// a UTC-vs-server-local "today" miscalculation in <c>BrowseRecipesQuery</c> (plantry-lgbu) that showed stock
/// as expired, and recipes as not cookable, a day early on any server west of UTC, and the same class of bug
/// recurring via <c>.LocalDateTime</c> at eleven further call sites (plantry-l639).
///
/// <para>This test reads the C# source of the domain/application bounded contexts under <c>src/Plantry.*</c>,
/// <b>derived</b> at scan time via <see cref="DiscoverScannedProjects"/> rather than hardcoded, minus
/// <see cref="ExcludedProjects"/> — the same domain-purity boundary <see cref="BoundaryTests"/> already
/// enforces (deliberately excluding every <c>*.Infrastructure</c> project, <c>Plantry.Web</c>,
/// <c>Plantry.Composition</c>, <c>Plantry.Migration.Grocy</c>, <c>Plantry.AppHost</c>, <c>Plantry.Migrator</c>,
/// and <c>Plantry.ServiceDefaults</c> — widening reach to those is a separate, unadjudicated decision, not this
/// guard's). A new bounded context added under <c>src/</c> is scanned automatically instead of silently
/// falling outside a hardcoded list.</para>
///
/// <para><b>Precision is the point.</b> Six forbidden shapes are <i>type-qualified</i> forms —
/// <c>DateTime.UtcNow</c>, <c>DateTime.Now</c>, <c>DateTime.Today</c>, <c>DateTimeOffset.UtcNow</c>,
/// <c>DateTimeOffset.Now</c>, <c>TimeProvider.System</c> — which can never match the sanctioned
/// <c>clock.UtcNow</c> idiom (or <c>_clock.UtcNow</c>). Two are <i>member-name</i> forms —
/// <c>.LocalDateTime</c> and <c>.ToLocalTime()</c> — which by design <b>do</b> fire even off an injected
/// clock (<c>clock.UtcNow.LocalDateTime</c>, <c>clock.UtcNow.ToLocalTime()</c>): the instant is injected but
/// the conversion still reads the machine zone — use the <c>ClockExtensions</c> seam instead. The ninth,
/// <c>TimeZoneInfo.Local</c>, names the machine zone directly. A guard that fired on <c>clock.UtcNow</c>
/// itself would be worse than no guard at all, so <see cref="Negative_BenignPatterns_AreNotFlagged"/> pins
/// that boundary as hard as <see cref="Positive_ForbiddenPatterns_AreDetected"/> pins reach.</para>
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
    /// <see cref="BoundaryTests"/> enforces), <b>derived</b> from every <c>src/Plantry.*</c> directory minus
    /// <see cref="ExcludedProjects"/> — not hardcoded (plantry-vgze). A newly added bounded context under
    /// <c>src/</c> is covered by default instead of silently unscanned; a new non-domain project must be added
    /// to <see cref="ExcludedProjects"/> explicitly or it becomes scanned. Removal of a project this guard is
    /// currently scanning (rename, move, or newly matching an exclusion rule) is caught loudly by
    /// <see cref="RequiredProjects"/>, not silently absorbed here — unpoliced domain code is the failure this
    /// guard exists to prevent, in either direction.</summary>
    private static readonly string[] ExcludedProjects =
    [
        "Plantry.Web",
        "Plantry.Composition",
        "Plantry.Migration.Grocy",
        "Plantry.AppHost",
        "Plantry.Migrator",
        "Plantry.ServiceDefaults",
    ];

    /// <summary>The bounded contexts that must always be in the derived scan set. Discovery
    /// (<see cref="DiscoverScannedProjects"/>) remains the source of truth — a NEW context is still
    /// covered automatically — but a known one silently dropping out (renamed, moved, or newly
    /// matching an exclusion rule) fails loudly instead of shrinking the guard's reach in silence.
    /// On an intentional rename, update this list in the same commit.</summary>
    private static readonly string[] RequiredProjects =
    [
        "Plantry.Catalog", "Plantry.Deals", "Plantry.Housekeeping", "Plantry.Identity",
        "Plantry.Intake", "Plantry.Inventory", "Plantry.MealPlanning", "Plantry.Pricing",
        "Plantry.Recipes", "Plantry.SharedKernel", "Plantry.Shopping",
    ];

    /// <summary>Enumerates <c>src/Plantry.*</c>, drops every <c>*.Infrastructure</c> project and everything in
    /// <see cref="ExcludedProjects"/>, and returns what's left — the domain/application bounded contexts this
    /// guard scans. Reach is identical to the formerly hardcoded eleven-project list.</summary>
    private static string[] DiscoverScannedProjects(string repoRoot) =>
        Directory.EnumerateDirectories(Path.Combine(repoRoot, "src"), "Plantry.*")
            .Select(Path.GetFileName)
            .Where(name => name is not null
                        && !name.EndsWith(".Infrastructure", StringComparison.Ordinal)
                        && !ExcludedProjects.Contains(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    // The forbidden shapes — six type-qualified ambient-clock reads, two member-name machine-zone
    // conversions, and TimeZoneInfo.Local itself (the 6/2/1 split in the class doc). Each type-qualified
    // entry requires the literal type name immediately
    // followed by ".UtcNow" / ".Now" / ".Today" — "clock.UtcNow" and "_clock.UtcNow" contain no
    // "DateTime"/"DateTimeOffset"/"TimeProvider" substring at all, so they can never match. A word boundary
    // before the type name additionally guards against a hypothetical longer identifier ending in
    // "...DateTime" being misread as the BCL type. Requiring the dot also keeps this from matching a property
    // DECLARATION shaped like "DateTimeOffset UtcNow { get; }" (space, not dot, between the type and the
    // member name — AC9).
    //
    // ".LocalDateTime" and ".ToLocalTime()" (plantry-l639) are member-name matches rather than type-qualified:
    // unlike the shapes above they aren't preceded by a fixed BCL type name (they hang off any
    // DateTimeOffset-typed expression, including "clock.UtcNow"), so a leading word boundary on the member name
    // itself is what keeps this precise without needing to enumerate every possible receiver.
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"\bDateTime\.UtcNow\b", RegexOptions.Compiled),
        new(@"\bDateTime\.Now\b", RegexOptions.Compiled),
        new(@"\bDateTime\.Today\b", RegexOptions.Compiled),
        new(@"\bDateTimeOffset\.UtcNow\b", RegexOptions.Compiled),
        new(@"\bDateTimeOffset\.Now\b", RegexOptions.Compiled),
        new(@"\bTimeProvider\.System\b", RegexOptions.Compiled),
        new(@"\.LocalDateTime\b", RegexOptions.Compiled),
        new(@"\bTimeZoneInfo\.Local\b", RegexOptions.Compiled),
        new(@"\.ToLocalTime\(", RegexOptions.Compiled),
    ];

    /// <summary>
    /// The single predicate every caller shares: true when <paramref name="line"/> contains any forbidden
    /// ambient-clock shape. The tree scan and both theories delegate here so reach and precision are pinned to
    /// the exact patterns that ship.
    /// </summary>
    private static bool IsOffendingLine(string line) =>
        ForbiddenPatterns.Any(p => p.IsMatch(line));

    [Fact(DisplayName = "No ambient clock read (DateTime/DateTimeOffset UtcNow/Now/Today, TimeProvider.System) or machine-zone read (.LocalDateTime, .ToLocalTime(), TimeZoneInfo.Local) outside IClock's SystemClock")]
    public void DomainAndApplicationLayers_HaveNoAmbientClockReads()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();
        var scannedProjects = DiscoverScannedProjects(repoRoot);

        var missing = RequiredProjects.Except(scannedProjects).ToArray();
        Assert.True(missing.Length == 0,
            "Bounded contexts that must be scanned are no longer discovered under src/Plantry.*: "
            + string.Join(", ", missing)
            + ". If a project was intentionally renamed or moved, update AmbientClockGuardTests.RequiredProjects "
            + "in the same commit; otherwise the ambient-clock guard is silently under-scanning.");

        foreach (var project in scannedProjects)
        {
            var projectRoot = Path.Combine(repoRoot, "src", project);

            foreach (var file in EnumerateSourceFiles(projectRoot))
            {
                // The sole sanctioned ambient reads: SystemClock's own IClock implementation — UtcNow's real
                // wall-clock read (plantry-lgbu) and Zone's TimeZoneInfo.Local (plantry-l639) — which exist
                // precisely so no other caller has to make either read.
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

    // Every forbidden shape the guard exists to catch — the six type-qualified BCL forms (both date types'
    // Now/UtcNow/Today plus the ambient TimeProvider.System singleton), the two member-name machine-zone
    // conversions (.LocalDateTime, .ToLocalTime()), and TimeZoneInfo.Local itself. If a refactor weakens a
    // pattern, one of these fails loudly.
    [Theory(DisplayName = "Guard flags every forbidden ambient-clock shape")]
    [InlineData(@"public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;")] // the exact plantry-lgbu bug
    [InlineData(@"var today = DateOnly.FromDateTime(DateTime.UtcNow);")]                // the exact BrowseRecipesQuery bug
    [InlineData(@"var stamp = DateTime.Now;")]
    [InlineData(@"var stamp = DateTimeOffset.Now;")]
    [InlineData(@"if (session?.CreatedAt is null) return DateTimeOffset.UtcNow;")]
    [InlineData(@"var today = DateTime.Today;")]                                    // DateTime.Now.Date-equivalent ambient read
    [InlineData(@"var utcNow = TimeProvider.System.GetUtcNow();")]                  // the un-injected TimeProvider singleton
    [InlineData(@"var today = DateOnly.FromDateTime(clock.UtcNow.LocalDateTime);")] // the machine-zone read plantry-l639 abolished — use clock.ToLocalDate(...)
    [InlineData(@"var local = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local);")] // the machine-zone read the IClock.Zone seam replaces
    [InlineData(@"var today = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());")] // the machine-zone conversion the ClockExtensions seam replaces — use clock.ToLocal(...)
    public void Positive_ForbiddenPatterns_AreDetected(string line) =>
        Assert.True(IsOffendingLine(line), $"Guard should have flagged: {line}");

    // The sanctioned idiom and adjacent-but-benign shapes that must NEVER be flagged. This is the precision half
    // of the guard: a false positive on the codebase's own sanctioned pattern is worse than no guard at all.
    [Theory(DisplayName = "Guard does not flag the sanctioned clock.UtcNow idiom or benign look-alikes")]
    [InlineData(@"var now = clock.UtcNow;")]                                     // the sanctioned idiom
    [InlineData(@"var now = _clock.UtcNow;")]                                    // field-backed variant
    [InlineData(@"public interface IClock { DateTimeOffset UtcNow { get; } }")]     // property DECLARATION, not a read
    [InlineData(@"DateTimeOffset UtcNow { get; }")]                                 // same, on its own line
    [InlineData(@"var now = timeProvider.GetUtcNow();")]                            // injected TimeProvider instance, not the ambient singleton
    // The guard scans raw lines, comments included: prose that merely DESCRIBES a sanctioned idiom must not
    // fire, but prose containing a forbidden token verbatim IS flagged by design — reword such comments (as
    // ClockExtensions' own class doc does) rather than weakening a pattern.
    [InlineData(@"// Mirrors the clock.ToLocalDate(clock.UtcNow) idiom used elsewhere.")]
    public void Negative_BenignPatterns_AreNotFlagged(string line) =>
        Assert.False(IsOffendingLine(line), $"Guard should NOT have flagged: {line}");

    /// <summary>True for <c>src/Plantry.SharedKernel/Domain/IClock.cs</c> — the sole exemption, matched by file
    /// path (mirrors how <c>MoneyFormattingGuardTests</c> exempts <c>MoneyDisplay.cs</c>). <c>SystemClock</c>
    /// lives in this same file and is where the ambient reads (wall clock and machine zone) legitimately
    /// happen.</summary>
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
