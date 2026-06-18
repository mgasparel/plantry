using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Tests.Web.Preferences;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// ADR-013 OOB-contract tests for the budget cost chip (plantry-pg6).
///
/// The .plan-bar lives OUTSIDE #plan-main-content, so the cost chip went stale after
/// full-grid htmx swaps (Generate/AcceptAll/Discard/AcceptCell/RejectCell/Move/Grid).
/// plantry-pg6 gives the chip a stable id (plan-cost-chip) and pins the OOB delivery
/// on the full-grid mutation paths via OobContract — the same primitive Intake uses.
///
/// Acceptance criteria (plantry-pg6):
///   1. Every full-grid mutation handler (Generate, AcceptAll, Discard) carries plan-cost-chip.
///   2. The week-navigation grid fragment (GET Grid) carries plan-cost-chip (nav-path coverage).
///   3. The chip is rendered inline (no hx-swap-oob) on the full page GET (first load).
///   4. The chip carries the .budget-chip class (MealCardEnrichmentTests contract preserved).
/// </summary>
[Collection(nameof(PlanCostChipOobCollection))]
public sealed class PlanCostChipOobContractTests(PlanCostChipOobFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    // ── 1. Full-grid mutation handlers carry plan-cost-chip ───────────────────

    [Fact(DisplayName = "POST Generate carries plan-cost-chip OOB (full-grid mutation — plantry-pg6)")]
    public async Task PostGenerate_CarriesPlanCostChip()
    {
        var client = CreateClient();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync("/MealPlan?handler=Generate", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // ADR-013 OOB-contract (plantry-pg6): the full-grid mutation response must carry
        // the plan-cost-chip projection so the budget chip is never stale after a swap.
        OobContract.AssertCarriesProjections(html, "plan-cost-chip");
    }

    [Fact(DisplayName = "POST AcceptAll carries plan-cost-chip OOB (full-grid mutation — plantry-pg6)")]
    public async Task PostAcceptAll_CarriesPlanCostChip()
    {
        var client = CreateClient();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync("/MealPlan?handler=AcceptAll", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        OobContract.AssertCarriesProjections(html, "plan-cost-chip");
    }

    [Fact(DisplayName = "POST Discard carries plan-cost-chip OOB (full-grid mutation — plantry-pg6)")]
    public async Task PostDiscard_CarriesPlanCostChip()
    {
        var client = CreateClient();

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync("/MealPlan?handler=Discard", form);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        OobContract.AssertCarriesProjections(html, "plan-cost-chip");
    }

    // ── 2. Week-nav grid fragment (GET Grid) carries plan-cost-chip ───────────

    [Fact(DisplayName = "GET Grid (week navigation) carries plan-cost-chip OOB (nav-path — plantry-pg6)")]
    public async Task GetGrid_CarriesPlanCostChip()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan?handler=Grid");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // ADR-013 OOB-contract (plantry-pg6): week navigation swaps the grid body;
        // plan-cost-chip must be re-emitted OOB so the chip reflects the navigated week.
        OobContract.AssertCarriesProjections(html, "plan-cost-chip");
    }

    // ── 3. First page load: chip is inline (no hx-swap-oob) ─────────────────

    [Fact(DisplayName = "GET /MealPlan (first load) renders plan-cost-chip inline without hx-swap-oob")]
    public async Task GetPage_RendersCostChipInline()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The chip must appear in the initial page.
        Assert.Contains("id=\"plan-cost-chip\"", html);

        // On first load the chip is rendered inline (Oob=false), so its element must NOT
        // carry hx-swap-oob="true". Verify by checking the chip element has no OOB attribute.
        // (The OOB elements elsewhere in the page may still carry hx-swap-oob on other responses,
        // but on a full page GET none of the plan-bar elements should have hx-swap-oob.)
        var chipIndex = html.IndexOf("id=\"plan-cost-chip\"", StringComparison.Ordinal);
        Assert.True(chipIndex >= 0, "plan-cost-chip not found in full page GET response.");
        // Read the element opening tag to check for hx-swap-oob (stop at closing >).
        var tagStart = html.LastIndexOf('<', chipIndex);
        var tagEnd = html.IndexOf('>', chipIndex);
        Assert.True(tagEnd > tagStart, "Could not isolate plan-cost-chip tag.");
        var tag = html[tagStart..tagEnd];
        Assert.DoesNotContain("hx-swap-oob", tag);
    }

    // ── 4. chip carries .budget-chip class (MealCardEnrichmentTests contract) ─

    [Fact(DisplayName = "GET /MealPlan plan-cost-chip carries the .budget-chip class (MealCardEnrichmentTests contract)")]
    public async Task GetPage_CostChip_HasBudgetChipClass()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // MealCardEnrichmentTests asserts "budget-chip" class presence (kept intact by plantry-pg6).
        Assert.Contains("budget-chip", html);

        // The chip element itself must carry the class — check the tag.
        var chipIndex = html.IndexOf("id=\"plan-cost-chip\"", StringComparison.Ordinal);
        Assert.True(chipIndex >= 0, "plan-cost-chip not found.");
        var tagStart = html.LastIndexOf('<', chipIndex);
        var tagEnd = html.IndexOf('>', chipIndex);
        var tag = html[tagStart..tagEnd];
        Assert.Contains("budget-chip", tag);
    }
}

[CollectionDefinition(nameof(PlanCostChipOobCollection))]
public sealed class PlanCostChipOobCollection : ICollectionFixture<PlanCostChipOobFactory> { }

/// <summary>
/// WAF factory for PlanCostChipOobContractTests. Extends WeekGridFragmentFactory with a
/// stubbed UserManager so the POST handlers (Generate/AcceptAll/Discard) can resolve the
/// current user without touching the real Identity Postgres DB.
/// </summary>
public sealed class PlanCostChipOobFactory : WeekGridFragmentFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            // POST handlers call GetCurrentUserIdAsync — stub UserManager off Identity DB.
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = "00000000-0000-0000-0000-0000000000bb" }));
        });
    }
}
