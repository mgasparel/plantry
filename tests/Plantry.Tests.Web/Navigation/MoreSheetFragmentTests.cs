using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Plantry.Tests.Web.Infrastructure;
using Xunit;

namespace Plantry.Tests.Web.Navigation;

/// <summary>
/// L4 fragment coverage for the mobile More sheet in <c>_Layout.cshtml</c> (plantry-kdvi). The sheet
/// replaced the deleted <c>/More</c> page and is the ticket's entire deliverable, so this is the only
/// place its rendered output — not just the pure <see cref="NavHighlight"/> predicate — is proven:
///   AC1 — every sidebar destination the sheet is meant to carry is present as a real link.
///   AC3 — Sign Out is a real antiforgery-protected POST, never a GET link.
///   AC7 — nothing renders an <c>href="/More"</c> (the route no longer exists).
///   AC9 — the bottom nav still has exactly 5 items (the More button opens a sheet, it doesn't add
///         a 6th navigable item).
/// <c>/Settings</c> is used as the page under test because its <c>OnGet</c> has no dependencies beyond
/// auth (mirrors the choice already made in <c>TidyUpBadgeSwrIntegrationTests</c>) — this suite isn't
/// trying to prove anything about Settings itself, only about the layout it's rendered inside of.
/// </summary>
public sealed class MoreSheetFragmentTests
{
    [Fact(DisplayName = "The More sheet carries a real link to every sidebar destination it owns (AC1)")]
    public async Task MoreSheet_ContainsEveryDestinationLink()
    {
        using var factory = new MoreSheetFactory();
        var client = factory.CreateAuthClient();

        var html = await (await client.GetAsync("/Settings")).Content.ReadAsStringAsync();

        // The desktop sidebar (also rendered by _Layout.cshtml) already carries every one of these
        // hrefs, so a plain Assert.Contains("href=\"/X\"", html) would still pass even if the whole
        // More-sheet block were deleted — it wouldn't actually be testing the sheet. Every sheet
        // anchor (and only a sheet anchor) carries x-on:click="moreOpen = false", so scoping the match
        // to that co-occurrence proves the link exists inside the sheet specifically.
        AssertSheetLink(html, "/MealPlan");
        AssertSheetLink(html, "/Deals");
        AssertSheetLink(html, "/Intake/Upload");
        AssertSheetLink(html, "/Pantry");
        AssertSheetLink(html, "/pantry/take-stock");
        AssertSheetLink(html, "/Catalog");
        AssertSheetLink(html, "/TidyUp");
        AssertSheetLink(html, "/Settings");
    }

    private static void AssertSheetLink(string html, string href)
    {
        var pattern = $"href=\"{Regex.Escape(href)}\"[^>]*x-on:click=\"moreOpen = false\"";
        Assert.Matches(pattern, html);
    }

    [Fact(DisplayName = "Sign Out in the sheet is a real antiforgery-protected POST form, never a GET link (AC3)")]
    public async Task SignOut_IsAnAntiforgeryProtectedPostForm()
    {
        using var factory = new MoreSheetFactory();
        var client = factory.CreateAuthClient();

        var html = await (await client.GetAsync("/Settings")).Content.ReadAsStringAsync();

        Assert.Contains("<form method=\"post\" action=\"/Account/Logout\"", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);

        // The sidebar footer already renders a byte-identical POST form to /Account/Logout — asserting
        // only "one exists" would still pass if the More sheet's own copy were deleted. Assert there are
        // TWO (sidebar's + sheet's), proving the sheet actually carries its own Sign Out form rather than
        // relying on the sidebar's (which is display:none below 767px, i.e. invisible wherever the sheet
        // itself would be reachable).
        Assert.Equal(2, Regex.Matches(html, "action=\"/Account/Logout\"").Count);
        Assert.Equal(2, Regex.Matches(html, "name=\"__RequestVerificationToken\"").Count);
    }

    [Fact(DisplayName = "The bottom nav still has exactly 5 items, and More is a dialog button, not a link to the deleted /More route (AC7/AC9)")]
    public async Task BottomNav_HasExactlyFiveItems_AndMoreIsADialogButton()
    {
        using var factory = new MoreSheetFactory();
        var client = factory.CreateAuthClient();

        var html = await (await client.GetAsync("/Settings")).Content.ReadAsStringAsync();

        Assert.Equal(5, Regex.Matches(html, "class=\"bottom-nav__item").Count);
        Assert.DoesNotContain("href=\"/More\"", html);
        // Razor's scoped-CSS pass injects a `b-xxxxxxx` attribute right after the tag name (e.g.
        // `<button b-70548fn17q type="button" ...>`), so the pattern can't assume `type="button"`
        // immediately follows `<button` — it asserts `type="button"` is present in the tag separately.
        var moreButton = Regex.Match(html, "<button[^>]*aria-label=\"More\"[^>]*aria-haspopup=\"dialog\"[^>]*>");
        Assert.True(moreButton.Success, "Expected a <button ... aria-label=\"More\" ... aria-haspopup=\"dialog\"> in the response.");
        Assert.Contains("type=\"button\"", moreButton.Value);
    }

    /// <summary>
    /// L4 WebApplicationFactory for the More sheet fragment tests. No fakes beyond auth are needed:
    /// <c>/Settings</c>'s <c>OnGet</c> touches nothing, and the layout's badge read is a cache miss
    /// (renders the zero-count/no-dot form) with the request-level refresh suppressed under the
    /// "Testing" environment — see the matching note in <c>_Layout.cshtml</c>.
    /// </summary>
    private sealed class MoreSheetFactory : WebApplicationFactory<Program>
    {
        private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

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
            });
        }

        public HttpClient CreateAuthClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
            return client;
        }
    }
}
