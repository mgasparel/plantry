using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Contract tests for the shared receipt-line price editor. DetailsStrip is rendered by both the
/// initial judgement-call card and the confirmed-line drawer, so one control must remain wired to the
/// LineState draft that buildSaveLineBody already posts to SaveLine.
/// </summary>
public sealed class IntakeReviewPriceControlTests
{
    [Fact]
    public void Shared_details_strip_renders_editable_price_bound_to_draft_price()
    {
        var js = File.ReadAllText(IslandPath());

        Assert.Contains("name=\"Edit.Price\"", js);
        Assert.Contains("value=${ls.draftPrice}", js);
        Assert.Contains("ls.draftPrice.value =", js);
    }

    [Fact]
    public void Price_control_is_reused_by_initial_card_and_confirmed_editor()
    {
        var js = File.ReadAllText(IslandPath());
        var deckStart = js.IndexOf("function DeckCard", StringComparison.Ordinal);
        var confirmedStart = js.IndexOf("function ConfirmedRow", StringComparison.Ordinal);
        var detailsUse = "<${DetailsStrip}";

        Assert.True(deckStart >= 0);
        Assert.True(confirmedStart > deckStart);
        Assert.Contains(detailsUse, js[deckStart..confirmedStart]);
        Assert.Contains(detailsUse, js[confirmedStart..]);
    }

    private static string IslandPath() => Path.Combine(
        WebSourceTree.RepoRoot(), "src", "Plantry.Web", "wwwroot", "js", "islands", "intake-review.js");
}
