using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Verify global configuration. Snapshots are committed <c>.verified.html</c> baselines under <c>Snapshots/</c>;
/// a fragment change that is not re-approved fails the test. Two sources of non-determinism are scrubbed so the
/// baselines stay stable while still failing on real markup drift:
///   • GUIDs (session id, line ids, derived DOM/listbox ids and form-action URLs) — replaced consistently by
///     Verify's inline-GUID scrubber, so structure is asserted without pinning random ids.
///   • the antiforgery <c>__RequestVerificationToken</c> hidden-input value — random per render.
/// </summary>
public static partial class VerifyModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
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
}
