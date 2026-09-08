using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// Contract tests for the Intake Review commit bar's discard control (plantry-08kc). The button
/// triggers the irreversible DiscardSessionCommand, not a no-op cancel, so it must be labeled
/// honestly and must confirm before it fires.
/// </summary>
public sealed class IntakeReviewCommitBarTests
{
    [Fact]
    public void Commit_bar_discard_button_is_labeled_Discard()
    {
        var js = File.ReadAllText(IslandPath());

        Assert.Contains(
            "class=\"btn btn--ghost commit-bar__cancel\" onClick=${handlers.discard}>Discard</button>",
            js);
    }

    [Fact]
    public void Discard_guards_on_confirm_before_posting_to_discardUrl()
    {
        var js = File.ReadAllText(IslandPath());
        var discardStart = js.IndexOf("function discard(", StringComparison.Ordinal);
        var postCall = js.IndexOf("postJson(hydration.discardUrl", StringComparison.Ordinal);

        Assert.True(discardStart >= 0);
        Assert.True(postCall > discardStart);

        var window = js[discardStart..postCall];
        Assert.Contains("confirm(", window);
    }

    private static string IslandPath() => Path.Combine(
        WebSourceTree.RepoRoot(), "src", "Plantry.Web", "wwwroot", "js", "islands", "intake-review.js");
}
