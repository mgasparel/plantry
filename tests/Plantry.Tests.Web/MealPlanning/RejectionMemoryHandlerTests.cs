using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 proof (plantry-l17y) that the MealPlan page handlers record rejections and feed them back into
/// generation: the planner is deterministic, so without the memory the same recipe is re-proposed.
/// Uses the real session-keyed pending store + real rejection memory over an in-memory cache, with two
/// otherwise-identical plated recipes so "a different recipe" is always available.
/// </summary>
public sealed class RejectionMemoryHandlerTests
{
    private static readonly string Week = SessionKeyedStoreFixture.WeekStart.ToString("yyyy-MM-dd");
    private static readonly string TargetDay = SessionKeyedStoreFixture.WeekStart.AddDays(2).ToString("yyyy-MM-dd");
    private static readonly string[] RecipeNames =
        [SessionKeyedTwoProposalFixture.RecipeDay0Name, SessionKeyedTwoProposalFixture.RecipeDay1Name];

    private static HttpClient NewClient(SessionKeyedTwoProposalFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, SessionKeyedStoreFixture.HouseholdId.ToString());
        return client;
    }

    private static async Task<string> PostAsync(HttpClient client, string url)
    {
        var page = await (await client.GetAsync($"/MealPlan?week={Week}")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "No antiforgery token found on the page.");
        var response = await client.PostAsync(url, new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("__RequestVerificationToken", token)]));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The recipe name rendered inside the target ghost-slot cell of the full page.</summary>
    private static async Task<string> GhostSlotRecipeAsync(HttpClient client, string date)
    {
        var html = await (await client.GetAsync($"/MealPlan?week={Week}")).Content.ReadAsStringAsync();
        var slotId = SessionKeyedStoreFixture.GhostSlot.Id.Value;
        var start = html.IndexOf($"id=\"cell-{slotId:N}-{date}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Target cell not found on the page.");
        var next = html.IndexOf("id=\"cell-", start + 10, StringComparison.Ordinal);
        var cell = next < 0 ? html[start..] : html[start..next];
        var shown = RecipeNames.Where(cell.Contains).ToList();
        return Assert.Single(shown);
    }

    private static string Cell(string handler, string date) =>
        $"/MealPlan?handler={handler}&date={date}&slotId={SessionKeyedStoreFixture.GhostSlot.Id.Value:D}&week={Week}";

    [Fact(DisplayName = "POST RegenerateCell remembers the replaced proposal and offers a different recipe")]
    public async Task RegenerateCell_OffersADifferentRecipe()
    {
        await using var factory = new SessionKeyedTwoProposalFactory();
        var client = NewClient(factory);

        await PostAsync(client, Cell("GenerateCell", TargetDay));
        var first = await GhostSlotRecipeAsync(client, TargetDay);

        await PostAsync(client, Cell("RegenerateCell", TargetDay));
        var second = await GhostSlotRecipeAsync(client, TargetDay);

        Assert.NotEqual(first, second);
    }

    [Fact(DisplayName = "POST RejectCell then GenerateCell does not re-propose the rejected recipe")]
    public async Task RejectCell_ThenGenerateCell_DoesNotRepeatRejectedRecipe()
    {
        await using var factory = new SessionKeyedTwoProposalFactory();
        var client = NewClient(factory);

        await PostAsync(client, Cell("GenerateCell", TargetDay));
        var rejected = await GhostSlotRecipeAsync(client, TargetDay);

        await PostAsync(client, Cell("RejectCell", TargetDay));
        await PostAsync(client, Cell("GenerateCell", TargetDay));
        var next = await GhostSlotRecipeAsync(client, TargetDay);

        Assert.NotEqual(rejected, next);
    }

    [Fact(DisplayName = "POST Generate (just today) twice offers a different recipe for the same cell the second time")]
    public async Task GenerateJustToday_Twice_OffersADifferentRecipe()
    {
        await using var factory = new SessionKeyedTwoProposalFactory();
        var client = NewClient(factory);
        var url = $"/MealPlan?handler=Generate&scope=today&week={TargetDay}";

        await PostAsync(client, url);
        var first = await GhostSlotRecipeAsync(client, TargetDay);

        await PostAsync(client, url);
        var second = await GhostSlotRecipeAsync(client, TargetDay);

        Assert.NotEqual(first, second);
    }

    [Fact(DisplayName = "POST Discard remembers the discarded proposals so the next Generate offers a different recipe")]
    public async Task Discard_ThenGenerate_DoesNotRepeatDiscardedRecipe()
    {
        await using var factory = new SessionKeyedTwoProposalFactory();
        var client = NewClient(factory);
        var generate = $"/MealPlan?handler=Generate&scope=today&week={TargetDay}";

        await PostAsync(client, generate);
        var first = await GhostSlotRecipeAsync(client, TargetDay);

        await PostAsync(client, $"/MealPlan?handler=Discard&week={Week}");
        await PostAsync(client, generate);
        var second = await GhostSlotRecipeAsync(client, TargetDay);

        Assert.NotEqual(first, second);
    }
}
