using Plantry.Web.Pages.Intake;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L1 tests for the pure "This month" card formatters on <see cref="UploadModel"/> (plantry-bzyr):
/// currency rendering (always <c>$0.00</c> when empty) and the humanized average-review-time footer
/// ("2m 40s" a minute or over, "48s" under a minute, an em-dash when null). These are the load-bearing
/// rendering rules the acceptance criteria call out, isolated from page composition and culture.
/// </summary>
public sealed class UploadStatsFormattingTests
{
    // ── FormatMoney ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "FormatMoney — zero renders $0.00")]
    public void FormatMoney_Zero_RendersDollarZero()
    {
        Assert.Equal("$0.00", UploadModel.FormatMoney(0m));
    }

    [Theory(DisplayName = "FormatMoney — two decimals with a leading $")]
    [InlineData(482.19, "$482.19")]
    [InlineData(7, "$7.00")]
    [InlineData(1000.5, "$1000.50")]
    public void FormatMoney_NonZero_TwoDecimals(decimal amount, string expected)
    {
        Assert.Equal(expected, UploadModel.FormatMoney(amount));
    }

    // ── FormatReviewTime ──────────────────────────────────────────────────────

    [Fact(DisplayName = "FormatReviewTime — null renders an em-dash")]
    public void FormatReviewTime_Null_RendersEmDash()
    {
        Assert.Equal("—", UploadModel.FormatReviewTime(null));
    }

    [Theory(DisplayName = "FormatReviewTime — under a minute renders bare seconds")]
    [InlineData(48, "48s")]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    public void FormatReviewTime_UnderAMinute_BareSeconds(int seconds, string expected)
    {
        Assert.Equal(expected, UploadModel.FormatReviewTime(TimeSpan.FromSeconds(seconds)));
    }

    [Theory(DisplayName = "FormatReviewTime — a minute or over renders 'Nm Ns'")]
    [InlineData(160, "2m 40s")]
    [InlineData(60, "1m 0s")]
    [InlineData(125, "2m 5s")]
    public void FormatReviewTime_AtLeastAMinute_MinutesAndSeconds(int seconds, string expected)
    {
        Assert.Equal(expected, UploadModel.FormatReviewTime(TimeSpan.FromSeconds(seconds)));
    }

    [Fact(DisplayName = "FormatReviewTime — rounds to whole seconds")]
    public void FormatReviewTime_RoundsToWholeSeconds()
    {
        // 159.6s rounds to 160 → 2m 40s (proves the average isn't truncated oddly).
        Assert.Equal("2m 40s", UploadModel.FormatReviewTime(TimeSpan.FromSeconds(159.6)));
    }
}
