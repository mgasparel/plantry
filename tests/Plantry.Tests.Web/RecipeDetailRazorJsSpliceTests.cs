using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 test double for <see cref="IQuantityFormatter"/> that returns a hostile, HTML/JS-significant
/// amount string for every requested quantity, regardless of key — simulating the "future validation
/// relaxation" the plantry-97jd investigation record flags as the real attack surface
/// (<c>Unit.Create</c> has no character-class constraint on unit codes, so a <c>"</c>-bearing amount is
/// not categorically impossible, only absent today). Deliberately NOT <see cref="FakeQuantityFormatter"/>,
/// which formats through the real numeric-only <c>QuantityFormatting</c> logic and could never emit this.
/// </summary>
file sealed class HostileQuantityFormatter : IQuantityFormatter
{
    // The 11-character hostile amount agreed in the plantry-97jd spec: a double quote (the truncation
    // trigger), an ampersand (entity-decode fidelity), and an opening angle bracket (tag-injection).
    private const string HostileAmount = "1\" jar & <b";

    public Task<IReadOnlyDictionary<string, FormattedQuantity>> FormatAsync(
        IReadOnlyList<QuantityFormatRequest> requests, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, FormattedQuantity> result = requests.ToDictionary(
            r => r.Key,
            r => new FormattedQuantity(HostileAmount, r.UnitId));
        return Task.FromResult(result);
    }
}

/// <summary>
/// Variant of <see cref="RecipeDetailFragmentFactory"/> that re-registers <see cref="IQuantityFormatter"/>
/// with <see cref="HostileQuantityFormatter"/> so every ingredient row's <c>DisplayQuantity</c> is a
/// <c>"</c>/<c>&amp;</c>/<c>&lt;</c>-bearing string, for the plantry-97jd splice-site test.
/// </summary>
public sealed class RecipeDetailHostileQuantityFactory : RecipeDetailFragmentFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Re-register over the base factory's AddFakeQuantityFormatter registration.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQuantityFormatter>();
            services.AddSingleton<IQuantityFormatter>(new HostileQuantityFormatter());
        });
    }
}

/// <summary>
/// L4 splice-site test proving <c>RazorJs.Literal</c>'s HTML-significant-character escaping (plantry-97jd)
/// keeps the Details page's <c>x-text</c> attribute intact even when fed a hostile
/// <c>DisplayQuantity</c> — the fourth appearance of the Html.Raw/attribute-truncation defect class
/// (prior: plantry-gcpb, plantry-wcmg, plantry-qrg7, all in <c>x-data</c>; this one is <c>x-text</c>).
/// Not reachable today (the investigation record traced every live input to numeric-formatter output
/// with no <c>"</c>/<c>&amp;</c>/<c>&lt;</c> in its alphabet) — this proves the helper's escaping, not
/// the accidental absence of dangerous input, is what keeps the splice site safe.
/// </summary>
public sealed class RecipeDetailRazorJsSpliceTests(RecipeDetailHostileQuantityFactory factory)
    : IClassFixture<RecipeDetailHostileQuantityFactory>
{
    [Fact]
    public async Task Hostile_DisplayQuantity_Renders_Intact_Escaped_Literal_Not_Truncated()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());

        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The escaped JS literal survives intact: RazorJs.Literal turned the hostile amount's '"', '&',
        // '<' into JS-unicode escapes before @Html.Raw spliced it into the double-quoted x-text
        // attribute — these are the literal backslash/u/hex-digit characters in the HTML source, not a
        // decoded quote.
        Assert.Contains("'1\\u0022 jar \\u0026 \\u003Cb'", html, StringComparison.Ordinal);

        // The truncation signature (plantry-gcpb/wcmg/qrg7): a raw '"' immediately after the opening
        // "'1" would mean the attribute ended early, right after the digit. Must not appear.
        Assert.DoesNotContain("x-text=\"scale === 1 ? '1\"", html, StringComparison.Ordinal);

        // The server-rendered fallback span text (plain @displayText, Razor-encoded — not @Html.Raw)
        // must show the ordinary HTML-encoded form.
        Assert.Contains("1&quot; jar &amp; &lt;b", html, StringComparison.Ordinal);
    }
}
