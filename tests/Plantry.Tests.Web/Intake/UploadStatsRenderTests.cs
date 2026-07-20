using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 render assertions for the Upload page "This month" card (plantry-bzyr), exercised through the real
/// <c>Plantry.Web</c> pipeline via <see cref="UploadFragmentFactory"/> (session repo and inventory counts
/// stubbed — no database). With no committed sessions and no stock the card must render its empty state:
/// <c>$0.00</c> groceries, zero counts, an em-dash review-time footer, and the relabelled "Expiring soon"
/// stat (the horizon is household-configurable, so the old "Expiring this week" wording is gone).
/// </summary>
public sealed class UploadStatsRenderTests(UploadFragmentFactory factory) : IClassFixture<UploadFragmentFactory>
{
    private const string UploadUrl = "/Intake/Upload";

    private async Task<string> GetUploadHtmlAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        return await (await client.GetAsync(UploadUrl)).Content.ReadAsStringAsync();
    }

    [Fact(DisplayName = "This month card — empty state renders $0.00 groceries")]
    public async Task EmptyState_RendersZeroCurrency()
    {
        var html = await GetUploadHtmlAsync();
        Assert.Contains("$0.00", html);
        Assert.Contains("Groceries logged", html);
    }

    [Fact(DisplayName = "This month card — 'Expiring soon' label replaces 'Expiring this week'")]
    public async Task ExpiringStat_IsRelabelledExpiringSoon()
    {
        var html = await GetUploadHtmlAsync();
        Assert.Contains("Expiring soon", html);
        Assert.DoesNotContain("Expiring this week", html);
    }

    [Fact(DisplayName = "This month card — empty month renders an em-dash review-time footer")]
    public async Task EmptyMonth_RendersDashReviewFooter()
    {
        var html = await GetUploadHtmlAsync();
        Assert.Contains("Average review time this month:", html);
        // Razor's default HtmlEncoder emits the em-dash (U+2014) as a numeric entity.
        Assert.True(
            html.Contains("<strong>&#x2014;</strong>") || html.Contains("<strong>—</strong>"),
            "The empty-month footer must render an em-dash in a <strong> element.");
    }
}
