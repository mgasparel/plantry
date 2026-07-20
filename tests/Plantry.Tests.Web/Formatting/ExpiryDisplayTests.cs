using Plantry.Web.Pages.Shared;

namespace Plantry.Tests.Web.Formatting;

/// <summary>
/// Unit tests for <see cref="ExpiryDisplay"/> (plantry-fdoq) — the single source of truth for expiry wording
/// and colour tier shared by the Today rail, Recipe ingredient rows, and the Pantry grid. Proves the relative
/// wording ("Expired Nd ago" / "Today" / "Tomorrow" / "in Nd"), the existing 3-tier colour thresholds reused
/// verbatim (urgent ≤1 / soon ≤3 / ok ≥4), the tier boundaries at 1d/3d/4d, and that
/// <see cref="ExpiryDisplay.Format(DateOnly, DateOnly)"/> is a pure function of the day delta.
/// </summary>
public sealed class ExpiryDisplayTests
{
    [Theory(DisplayName = "FromDaysUntilExpiry — wording + tier for each signed day delta")]
    // Expired (negative): urgent tier, "Expired Nd ago".
    [InlineData(-25, "Expired 25d ago", "urgent")]
    [InlineData(-1, "Expired 1d ago", "urgent")]
    // Same-day expiry is treated as not-yet-past everywhere in Plantry (IsExpired = date < today is strict),
    // so 0 renders "Today", not "Expired today" — resolving the today/expired edge consistently.
    [InlineData(0, "Today", "urgent")]
    // 1d boundary — still urgent (≤1), "Tomorrow".
    [InlineData(1, "Tomorrow", "urgent")]
    [InlineData(2, "in 2d", "soon")]
    // 3d boundary — last soon day (≤3).
    [InlineData(3, "in 3d", "soon")]
    // 4d boundary — first ok day (≥4).
    [InlineData(4, "in 4d", "ok")]
    [InlineData(6, "in 6d", "ok")]
    public void FromDaysUntilExpiry_ProducesExpectedLabelAndTier(int days, string expectedLabel, string expectedTier)
    {
        var (label, tier) = ExpiryDisplay.FromDaysUntilExpiry(days);
        Assert.Equal(expectedLabel, label);
        Assert.Equal(expectedTier, tier);
    }

    [Theory(DisplayName = "Format(date, today) — delegates to the day delta (soonestExpiry - today)")]
    [InlineData(-25, "Expired 25d ago", "urgent")]
    [InlineData(0, "Today", "urgent")]
    [InlineData(1, "Tomorrow", "urgent")]
    [InlineData(3, "in 3d", "soon")]
    [InlineData(4, "in 4d", "ok")]
    public void Format_FromDates_MatchesDayDelta(int offset, string expectedLabel, string expectedTier)
    {
        var today = new DateOnly(2026, 7, 20);
        var (label, tier) = ExpiryDisplay.Format(today.AddDays(offset), today);
        Assert.Equal(expectedLabel, label);
        Assert.Equal(expectedTier, tier);
    }

    [Fact(DisplayName = "Format — is independent of the absolute dates, only the delta matters")]
    public void Format_DependsOnlyOnDelta()
    {
        var a = ExpiryDisplay.Format(new DateOnly(2026, 1, 4), new DateOnly(2026, 1, 1)); // +3
        var b = ExpiryDisplay.Format(new DateOnly(2030, 12, 28), new DateOnly(2030, 12, 25)); // +3
        Assert.Equal(a, b);
        Assert.Equal(("in 3d", "soon"), a);
    }
}
