using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Housekeeping;

/// <summary>
/// L4 fragment tests for the Tidy Up page's dismiss/restore htmx contract (plantry-ygti; finding from
/// plantry-66xs pre-flight). Proves that <c>IndexModel.OnPostDismissAsync</c>/<c>OnPostRestoreAsync</c>
/// (src/Plantry.Web/Pages/TidyUp/Index.cshtml.cs) return <c>_DismissResult.cshtml</c>'s exact
/// fragment composition: the refreshed <c>_Groups</c> body plus BOTH nav badge spans, out-of-band
/// swapped with the ids <c>sidebar-tidyup-badge</c> and <c>more-tidyup-badge</c> (T6/T7). A silent
/// id/targetId drift here would break the badge update with no other test catching it — command/query
/// logic is already covered at L1 (GetTidyUpPageQueryTests, DismissFindingCommandTests,
/// RestoreFindingCommandTests) but the page-model glue and fragment shape were not.
///
/// Uses the WAF harness with in-memory fakes for the detector catalogue and the dismissal repository —
/// no Postgres touched. Each test builds its own factory instance (rather than a shared class fixture)
/// so the single seeded finding's dismiss/restore state never leaks between tests.
/// </summary>
public sealed class TidyUpFragmentTests
{
    private static readonly Guid HouseholdId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    [Fact(DisplayName = "GET /TidyUp renders the seeded open finding")]
    public async Task Get_Index_RendersOpenFinding()
    {
        using var factory = new TidyUpFragmentFactory();
        var client = factory.CreateAuthClient(HouseholdId);

        var resp = await client.GetAsync("/TidyUp");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Test Widget", html);
        Assert.DoesNotContain("All tidy", html);
    }

    [Fact(DisplayName = "POST Dismiss returns refreshed _Groups body plus both OOB badge spans with correct ids (T6/T7)")]
    public async Task PostDismiss_ReturnsGroupsAndBothOobBadges()
    {
        using var factory = new TidyUpFragmentFactory();
        var client = factory.CreateAuthClient(HouseholdId);

        var pageHtml = await (await client.GetAsync("/TidyUp")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var content = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token),
            new("detectorId", TidyUpFragmentFactory.TestDetectorId.Value),
            new("subjectId", TidyUpFragmentFactory.SubjectId.ToString()),
            new("fingerprint", TidyUpFragmentFactory.FactsFingerprint),
        ]);

        var resp = await client.PostAsync("/TidyUp?handler=Dismiss", content);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // Refreshed _Groups body: the only finding is now dismissed, so the "All tidy" empty state
        // shows, and the finding reappears under the flat dismissed disclosure.
        Assert.Contains("All tidy", html);
        Assert.Contains("Dismissed (1)", html);

        // Both OOB badge spans must be present with the exact ids the layout's two render locations
        // (desktop sidebar + mobile More hub) key their htmx swap targets on.
        AssertOobBadge(html, "sidebar-tidyup-badge");
        AssertOobBadge(html, "more-tidyup-badge");

        // Dismissing the only open finding drops the open count to 0 — both badges render the
        // invisible (display:none) zero-count span, but still carry hx-swap-oob so the visible
        // count from a moment ago is actually cleared rather than left stale.
        Assert.DoesNotMatch("id=\"sidebar-tidyup-badge\"[^>]*>1<", html);
        Assert.DoesNotMatch("id=\"more-tidyup-badge\"[^>]*>1<", html);
    }

    [Fact(DisplayName = "POST Restore returns refreshed _Groups body plus both OOB badge spans carrying the restored count (T6/T7)")]
    public async Task PostRestore_ReturnsGroupsAndBothOobBadges()
    {
        using var factory = new TidyUpFragmentFactory();
        var client = factory.CreateAuthClient(HouseholdId);

        // Dismiss first so there is a tombstone to restore.
        var pageHtml = await (await client.GetAsync("/TidyUp")).Content.ReadAsStringAsync();
        var dismissToken = ExtractAntiforgeryToken(pageHtml);
        var dismissContent = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", dismissToken),
            new("detectorId", TidyUpFragmentFactory.TestDetectorId.Value),
            new("subjectId", TidyUpFragmentFactory.SubjectId.ToString()),
            new("fingerprint", TidyUpFragmentFactory.FactsFingerprint),
        ]);
        await client.PostAsync("/TidyUp?handler=Dismiss", dismissContent);

        // Re-fetch a fresh antiforgery token before the Restore POST.
        var page2Html = await (await client.GetAsync("/TidyUp")).Content.ReadAsStringAsync();
        var restoreToken = ExtractAntiforgeryToken(page2Html);
        var restoreContent = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", restoreToken),
            new("detectorId", TidyUpFragmentFactory.TestDetectorId.Value),
            new("subjectId", TidyUpFragmentFactory.SubjectId.ToString()),
        ]);

        var resp = await client.PostAsync("/TidyUp?handler=Restore", restoreContent);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // Refreshed _Groups body: the finding is open again — no more "All tidy", no more dismissed
        // disclosure (it was the only tombstone).
        Assert.Contains("Test Widget", html);
        Assert.DoesNotContain("All tidy", html);
        Assert.DoesNotContain("tidyup-dismissed", html);

        // Both OOB badge spans present with the exact ids, now carrying the restored count of 1.
        AssertOobBadge(html, "sidebar-tidyup-badge");
        AssertOobBadge(html, "more-tidyup-badge");
        Assert.Matches("id=\"sidebar-tidyup-badge\"[^>]*>1<", html);
        Assert.Matches("id=\"more-tidyup-badge\"[^>]*>1<", html);
    }

    [Fact(DisplayName = "Unauthenticated GET /TidyUp returns 401")]
    public async Task Unauthenticated_Index_Returns401()
    {
        using var factory = new TidyUpFragmentFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync("/TidyUp");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts the response carries a badge span with the given id and that it is out-of-band swapped
    /// (<c>hx-swap-oob="true"</c>) — the mechanism the desktop sidebar and mobile More hub both rely on
    /// to update independently from one dismiss/restore response (T6/T7, ADR-013).
    /// </summary>
    private static void AssertOobBadge(string html, string targetId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, $"<span id=\"{targetId}\"[^>]*>");
        Assert.True(match.Success, $"Expected an OOB badge span with id=\"{targetId}\" in the response.");
        Assert.Contains("hx-swap-oob=\"true\"", match.Value);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }
}

/// <summary>
/// L4 WebApplicationFactory for the Tidy Up dismiss/restore OOB fragment tests. Replaces the real
/// detector catalogue (which reads across Catalog/Inventory/Recipes and needs Postgres) with one fake
/// detector producing a single, stable finding, and the real EF-backed dismissal repository with an
/// in-memory fake — no database needed. <c>ITidyUpBadgeCache</c> is left as the real process-local
/// <c>TidyUpBadgeCache</c> singleton Program.cs already registers, so the badge count these tests
/// assert on is the same cache the production nav layout reads.
/// </summary>
public sealed class TidyUpFragmentFactory : WebApplicationFactory<Program>
{
    public static readonly DetectorId TestDetectorId = new("test-oob-detector");
    public static readonly Guid SubjectId = Guid.Parse("55555555-0000-0000-0000-500000000001");
    public const string FactsFingerprint = "fp-v1";

    public static Finding TestFinding => new(
        TestDetectorId,
        SubjectId,
        "Test Widget",
        "3 units affected",
        "Might quietly annoy you later.",
        "/Catalog",
        "Fix in Catalog",
        FactsFingerprint);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IDismissalRepository>();
            services.AddSingleton<IDismissalRepository>(new FakeDismissalRepository());

            // Drop the real detector catalogue (StockUnitUnconvertibleDetector, RecipeConversionGapDetector
            // — both need Postgres-backed readers) and register exactly one fake so GetTidyUpPageQuery's
            // IEnumerable<IProblemDetector> resolves to a single, controlled finding.
            services.RemoveAll<IProblemDetector>();
            services.AddSingleton<IProblemDetector>(new FakeProblemDetector(TestFinding));
        });
    }

    /// <summary>Creates an authenticated HTTP client for the given household.</summary>
    public HttpClient CreateAuthClient(Guid householdId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }
}

// ── fakes ─────────────────────────────────────────────────────────────────────

/// <summary>Single-finding fake detector — the OOB fragment contract needs exactly one controllable finding.</summary>
public sealed class FakeProblemDetector(Finding finding) : IProblemDetector
{
    public DetectorId Id => finding.DetectorId;
    public Severity Severity => Severity.Advisory;
    public string GroupTitle => "Test Findings";
    public string GroupConsequence => "Test consequence copy.";
    public string IconName => "i-scale";

    public Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Finding>>([finding]);
}

/// <summary>In-memory fake for <see cref="IDismissalRepository"/> — no Postgres/EF needed.</summary>
public sealed class FakeDismissalRepository : IDismissalRepository
{
    private readonly List<Dismissal> _items = [];

    public Task<IReadOnlyList<Dismissal>> ListForHouseholdAsync(
        HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Dismissal>>(
            _items.Where(d => d.HouseholdId == householdId).ToList());

    public Task<Dismissal?> FindAsync(
        HouseholdId householdId, DetectorId detectorId, Guid subjectId, CancellationToken ct = default) =>
        Task.FromResult(_items.FirstOrDefault(
            d => d.HouseholdId == householdId && d.DetectorId == detectorId && d.SubjectId == subjectId));

    public Task AddAsync(Dismissal dismissal, CancellationToken ct = default)
    {
        _items.Add(dismissal);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Dismissal dismissal, CancellationToken ct = default)
    {
        _items.Remove(dismissal);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
