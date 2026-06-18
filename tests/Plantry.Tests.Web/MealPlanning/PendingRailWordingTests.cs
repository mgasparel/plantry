using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.Tests.Web.Infrastructure;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// Verifies that when pending AI proposals are staged the insights rail wording
/// accurately reflects that the cost chip excludes pending suggestions (plantry-b4h).
///
/// Chosen semantics: option (b) — the budget chip counts CONFIRMED meals only.
/// The rail callout must NOT claim "These figures include pending suggestions";
/// it must instead state that pending suggestions are NOT included in the cost total.
///
/// Acceptance criteria (plantry-b4h):
///   1. Chip and rail agree: the wording must align with the chip's confirmed-only semantics.
///   2. The rail callout explicitly says pending suggestions are not included in the cost total.
///   3. The wording instructs the user to accept or discard to lock them in.
/// </summary>
[Collection(nameof(PendingRailWordingCollection))]
public sealed class PendingRailWordingTests(GhostCellFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "plantry-b4h: rail does NOT say figures include pending suggestions (chip excludes them)")]
    public async Task Rail_DoesNotClaimFiguresIncludePending()
    {
        var client = CreateClient();

        var html = await (await client.GetAsync("/MealPlan?handler=Grid")).Content.ReadAsStringAsync();

        // The incorrect wording from before the fix must not appear.
        Assert.DoesNotContain("These figures include pending suggestions", html);
    }

    [Fact(DisplayName = "plantry-b4h: rail callout says pending suggestions are not included in the cost total")]
    public async Task Rail_SaysPendingNotIncludedInCostTotal()
    {
        var client = CreateClient();

        var html = await (await client.GetAsync("/MealPlan?handler=Grid")).Content.ReadAsStringAsync();
        var doc = new HtmlParser().ParseDocument(html);

        // The pending-suggestions callout must appear (dashed border — only shown when PendingCount > 0)
        var callout = doc.QuerySelector("#plan-rail .callout[style*='border-style:dashed']");
        Assert.NotNull(callout);

        var body = callout!.QuerySelector(".co-body");
        Assert.NotNull(body);

        // Body must say that pending suggestions are NOT included in the cost total.
        Assert.Contains("not included in the cost total", body!.TextContent);
    }

    [Fact(DisplayName = "plantry-b4h: rail callout instructs user to accept or discard to lock in")]
    public async Task Rail_InstructsAcceptOrDiscardToLockIn()
    {
        var client = CreateClient();

        var html = await (await client.GetAsync("/MealPlan?handler=Grid")).Content.ReadAsStringAsync();
        var doc = new HtmlParser().ParseDocument(html);

        var callout = doc.QuerySelector("#plan-rail .callout[style*='border-style:dashed']");
        Assert.NotNull(callout);

        var body = callout!.QuerySelector(".co-body");
        Assert.NotNull(body);

        // Must still instruct the user what to do.
        Assert.Contains("Accept or discard to lock them in", body!.TextContent);
    }

    [Fact(DisplayName = "plantry-b4h: rail callout is only shown when there are pending proposals")]
    public async Task Rail_NoPendingCallout_WhenNoPendingProposals()
    {
        // Use the base WeekGridFragmentFactory — NullPendingProposalStore, no proposals staged.
        await using var emptyFactory = new WeekGridFragmentFactory();
        var emptyClient = emptyFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        emptyClient.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());

        var html = await (await emptyClient.GetAsync("/MealPlan?handler=Grid")).Content.ReadAsStringAsync();

        // No pending proposals → the dashed callout must not appear.
        Assert.DoesNotContain("border-style:dashed", html);
    }
}

[CollectionDefinition(nameof(PendingRailWordingCollection))]
public sealed class PendingRailWordingCollection : ICollectionFixture<GhostCellFactory> { }
