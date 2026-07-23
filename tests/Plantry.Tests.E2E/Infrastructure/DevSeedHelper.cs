using Microsoft.Playwright;

namespace Plantry.Tests.E2E.Infrastructure;

/// <summary>
/// Test-side helper for the additive dev seed endpoint (POST /Dev/Seed, Program.cs — runs
/// FakeDataSeeder, gated to Development, which is what <see cref="AppHostFixture"/> boots). This is
/// the cheap E2E setup path (plantry-abq5): instead of authoring products/recipes through the editor
/// UI in every test, call <see cref="SeedDemoDataAsync"/> once and navigate straight to content.
///
/// VERIFIED MECHANICS (plantry-abq5 scope item 1):
///
/// (a) FakeDataSeeder does NOT seed into the calling session's household. Its
///     <c>CreateHouseholdWithUserAsync</c> always runs <c>RegisterHouseholdCommand</c> to mint its OWN
///     household — the fixed demo one (<see cref="Plantry.Web.Dev.FakeDataSeeder.DemoEmail"/> /
///     <see cref="Plantry.Web.Dev.FakeDataSeeder.DemoPassword"/>) plus two random ones — regardless of
///     who (if anyone) is authenticated when /Dev/Seed is invoked: the endpoint has no
///     <c>[Authorize]</c> and never reads the caller's <c>TenantContext</c>. <c>SeedAsync</c> is also
///     short-circuiting/idempotent — it no-ops entirely once the demo user exists, so only the FIRST
///     call in a given AppHostFixture run actually writes.
///     CONSEQUENCE: a test that wants to see seeded content must sign in AS the demo user
///     (<see cref="Plantry.Web.Dev.FakeDataSeeder.DemoEmail"/>), not register its own fresh household
///     and expect seeded rows to land there. Dedicated authoring/tenancy-isolation journeys are
///     unaffected — they keep registering their own household and stay UI-driven (scope item 3).
///
/// (b) MapDevPost endpoints validate ASP.NET Core's standard antiforgery cookie+header pair (no seam
///     disables it for dev-only routes). <c>htmx-antiforgery.js</c> is how the /Dev/Endpoints page
///     invokes them: it reads the hidden <c>__RequestVerificationToken</c> input that
///     <c>@Html.AntiForgeryToken()</c> renders and attaches its value as a <c>RequestVerificationToken</c>
///     header. This helper mirrors that exactly, using <see cref="IPage.APIRequest"/> (which shares
///     cookie storage with the page's browser context, so the antiforgery cookie minted by the GET
///     below is present on the POST) instead of htmx.
/// </summary>
public static class DevSeedHelper
{
    /// <summary>
    /// POSTs <c>{baseUrl}/Dev/Seed</c> using <paramref name="page"/>'s own cookie jar and asserts the
    /// round-trip succeeded. Additive and idempotent — safe to call from every seed-consuming test;
    /// only the first call in a given AppHostFixture run performs any writes.
    /// </summary>
    public static async Task SeedDemoDataAsync(IPage page, string baseUrl)
    {
        // /Dev/Endpoints is unauthenticated and dev-only. The rendered page carries TWO hidden
        // __RequestVerificationToken inputs — one from the global _Layout.cshtml @Html.AntiForgeryToken()
        // (feeds htmx-antiforgery.js) and one from the page's own @Html.AntiForgeryToken(). Both are
        // minted against the same antiforgery cookie, so either value validates the POST; we take the
        // first (a bare locator here trips Playwright strict mode, which forbids a 2-element match). The
        // Set-Cookie from the GET lands in the page's browser context — the same cookie jar
        // IPage.APIRequest reads from.
        await page.GotoAsync($"{baseUrl}/Dev/Endpoints");
        var token = await page.Locator("input[name='__RequestVerificationToken']").First.InputValueAsync();

        // Generous timeout: on a cold AppHostFixture run this is the FIRST call that actually seeds —
        // three households' worth of catalog/recipes/inventory/pricing/meal-plan/deals data (the deals
        // fixture alone replays ~450 rows through real domain verbs). Every call after the first is a
        // fast no-op (FakeDataSeeder.SeedAsync short-circuits once the demo user exists).
        var response = await page.APIRequest.PostAsync($"{baseUrl}/Dev/Seed", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["RequestVerificationToken"] = token },
            Timeout = (float)TimeSpan.FromMinutes(2).TotalMilliseconds,
        });

        if (!response.Ok)
        {
            var body = await response.TextAsync();
            throw new InvalidOperationException(
                $"POST /Dev/Seed failed: {response.Status} {response.StatusText} — {body}");
        }
    }
}
