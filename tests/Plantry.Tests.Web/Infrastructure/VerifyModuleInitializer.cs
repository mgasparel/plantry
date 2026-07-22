using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DiffEngine;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Verify global configuration. Snapshots are committed <c>.verified.html</c> baselines under <c>Snapshots/</c>;
/// a fragment change that is not re-approved fails the test. Three sources of non-determinism are scrubbed so the
/// baselines stay stable while still failing on real markup drift:
///   • GUIDs (session id, line ids, derived DOM/listbox ids and form-action URLs) — replaced consistently by
///     Verify's inline-GUID scrubber, so structure is asserted without pinning random ids.
///   • the antiforgery <c>__RequestVerificationToken</c> hidden-input value — random per render.
///   • <c>PopoverTagHelper</c>'s <c>popover-N</c> ids — the counter is a process-wide static, so its value
///     depends on how many other popovers rendered earlier in the same test run (plantry-zxo4); scrubbed to a
///     stable per-document <c>popover-N</c> sequence so the baseline does not flake on test execution order.
/// </summary>
public static partial class VerifyModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Never launch an external diff tool (vim/gVim or any other) on snapshot mismatch. The test still
        // fails and the .received.* file is written for manual inspection; the diff-tool launch is noise on
        // local machines and wrong in CI.
        DiffRunner.Disabled = true;

        // Pin the culture so currency ("$2.99") and date ("1 Jul") formatting — which the row VM renders via
        // CurrentCulture — is identical on every machine/CI, keeping the committed baselines stable.
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // Replace every GUID (session id, line ids, derived DOM/listbox ids, form-action URLs) with a stable
        // Guid_N token, consistently across the snapshot — so the random per-run ids do not defeat the
        // baseline, while the structure (which id maps where) is still asserted.
        VerifierSettings.ScrubInlineGuids();

        VerifierSettings.AddScrubber(builder =>
        {
            var scrubbed = AntiforgeryTokenValue().Replace(
                builder.ToString(),
                @"__RequestVerificationToken${pre}value=""{scrubbed-antiforgery-token}""");
            builder.Clear();
            builder.Append(scrubbed);
        });

        // PopoverTagHelper.Id is generated from a process-wide static Interlocked counter, so its numeric
        // value drifts with how many other popovers rendered earlier in the SAME test run — not a property
        // of the markup under test. Remap each unique "popover-N" token to a stable, per-document sequence
        // (popover-1, popover-2, ...) in first-appearance order, mirroring ScrubInlineGuids' approach, so
        // aria-describedby/id pairs still assert their structural link without pinning the raw counter value.
        VerifierSettings.AddScrubber(builder =>
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var next = 0;
            var scrubbed = PopoverId().Replace(builder.ToString(), m =>
            {
                if (!map.TryGetValue(m.Value, out var token))
                {
                    token = $"popover-{++next}";
                    map[m.Value] = token;
                }
                return token;
            });
            builder.Clear();
            builder.Append(scrubbed);
        });

        // One-time bootstrap escape hatch: when PLANTRY_ACCEPT_SNAPSHOTS=1, write the received output as the
        // verified baseline instead of comparing. Used only to seed the initial committed baselines; normal
        // runs (and CI) leave it unset, so any later fragment drift fails the test.
        if (Environment.GetEnvironmentVariable("PLANTRY_ACCEPT_SNAPSHOTS") == "1")
            VerifierSettings.AutoVerify();

        // Store snapshots next to the tests in a Snapshots/ folder, grouped by test class/method.
        Verifier.DerivePathInfo((sourceFile, projectDirectory, type, method) =>
            new PathInfo(
                directory: Path.Combine(projectDirectory, "Snapshots"),
                typeName: type.Name,
                methodName: method.Name));
    }

    [GeneratedRegex(@"__RequestVerificationToken(?<pre>[^>]*?)value=""[^""]*""")]
    private static partial Regex AntiforgeryTokenValue();

    [GeneratedRegex(@"popover-\d+")]
    private static partial Regex PopoverId();
}
