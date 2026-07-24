using Plantry.Inventory.Domain;
using PantryProductPage = Plantry.Web.Pages.Pantry.Products.DetailModel;

namespace Plantry.Tests.Web.Grids;

/// <summary>
/// Unit tests for the "Mark opened" / "Open" badge / auto-open toast copy (plantry-1le6, UI spec §1/§3
/// and rule 5) — pins the pure formatting functions directly, mirroring
/// <see cref="StockDetailSourceCellTests"/>'s pattern of pinning a pure helper without a full page render.
/// </summary>
public sealed class MarkOpenedToastTests
{
    private static readonly StockEntryId LotId = StockEntryId.New();

    [Fact(DisplayName = "MarkOpened toast — a default applied states the new expiry")]
    public void FormatMarkOpenedToast_DefaultApplied_StatesNewExpiry()
    {
        var outcome = new MarkOpenedOutcome(LotId, new DateOnly(2026, 8, 5), DefaultApplied: true);

        var toast = PantryProductPage.FormatMarkOpenedToast(outcome);

        Assert.Equal("Opened — now expires 5 Aug 2026", toast);
    }

    [Fact(DisplayName = "MarkOpened toast — no default configured says the expiry is unchanged")]
    public void FormatMarkOpenedToast_NoDefault_SaysUnchanged()
    {
        var outcome = new MarkOpenedOutcome(LotId, new DateOnly(2026, 8, 5), DefaultApplied: false);

        var toast = PantryProductPage.FormatMarkOpenedToast(outcome);

        Assert.Equal("Opened — expiry unchanged", toast);
    }

    [Fact(DisplayName = "Unmarked toast — states the (unrestored) expiry honestly")]
    public void FormatUnmarkedToast_WithExpiry_StatesItStays()
    {
        var outcome = new UnmarkOpenedOutcome(LotId, new DateOnly(2026, 8, 5));

        var toast = PantryProductPage.FormatUnmarkedToast(outcome);

        Assert.Equal("Unmarked — expiry stays 5 Aug 2026", toast);
    }

    [Fact(DisplayName = "Unmarked toast — no expiry at all says so plainly")]
    public void FormatUnmarkedToast_NoExpiry_SaysNoExpirySet()
    {
        var outcome = new UnmarkOpenedOutcome(LotId, null);

        var toast = PantryProductPage.FormatUnmarkedToast(outcome);

        Assert.Equal("Unmarked — no expiry set", toast);
    }

    // ── BuildConsumeNotice (rule 5's fold-in to the existing consume-result notice) ─────────────

    [Fact(DisplayName = "BuildConsumeNotice — no shortfall, no auto-open → null (no notice at all)")]
    public void BuildConsumeNotice_PlainSuccess_ReturnsNull()
    {
        var outcome = new ConsumeOutcome([], 0m, Guid.NewGuid(), []);

        var notice = PantryProductPage.BuildConsumeNotice(outcome);

        Assert.Null(notice);
    }

    [Fact(DisplayName = "BuildConsumeNotice — shortfall only, unchanged from today's existing wording")]
    public void BuildConsumeNotice_ShortfallOnly()
    {
        var outcome = new ConsumeOutcome([], 2.5m, Guid.NewGuid(), []);

        var notice = PantryProductPage.BuildConsumeNotice(outcome);

        Assert.Equal("Consumed what was available — 2.5 could not be satisfied.", notice);
    }

    [Fact(DisplayName = "BuildConsumeNotice — auto-open only, with a default applied")]
    public void BuildConsumeNotice_AutoOpenOnly_DefaultApplied()
    {
        var outcome = new ConsumeOutcome(
            [], 0m, Guid.NewGuid(),
            [new MarkOpenedOutcome(LotId, new DateOnly(2026, 8, 5), DefaultApplied: true)]);

        var notice = PantryProductPage.BuildConsumeNotice(outcome);

        Assert.Equal("Marked opened — now expires 5 Aug 2026.", notice);
    }

    [Fact(DisplayName = "BuildConsumeNotice — auto-open only, no default configured")]
    public void BuildConsumeNotice_AutoOpenOnly_NoDefault()
    {
        var outcome = new ConsumeOutcome(
            [], 0m, Guid.NewGuid(),
            [new MarkOpenedOutcome(LotId, new DateOnly(2026, 8, 5), DefaultApplied: false)]);

        var notice = PantryProductPage.BuildConsumeNotice(outcome);

        Assert.Equal("Marked opened — expiry unchanged.", notice);
    }

    [Fact(DisplayName = "BuildConsumeNotice — shortfall AND auto-open fold into one combined notice")]
    public void BuildConsumeNotice_Shortfall_And_AutoOpen_Combine()
    {
        var outcome = new ConsumeOutcome(
            [], 1m, Guid.NewGuid(),
            [new MarkOpenedOutcome(LotId, new DateOnly(2026, 8, 5), DefaultApplied: true)]);

        var notice = PantryProductPage.BuildConsumeNotice(outcome);

        Assert.Equal(
            "Consumed what was available — 1 could not be satisfied. Marked opened — now expires 5 Aug 2026.",
            notice);
    }

    [Fact(DisplayName = "BuildConsumeNotice — a multi-lot auto-open gets a sentence per lot")]
    public void BuildConsumeNotice_MultipleAutoOpened_OneSentenceEach()
    {
        var outcome = new ConsumeOutcome(
            [], 0m, Guid.NewGuid(),
            [
                new MarkOpenedOutcome(StockEntryId.New(), new DateOnly(2026, 8, 5), DefaultApplied: true),
                new MarkOpenedOutcome(StockEntryId.New(), null, DefaultApplied: false),
            ]);

        var notice = PantryProductPage.BuildConsumeNotice(outcome);

        Assert.Equal(
            "Marked opened — now expires 5 Aug 2026. Marked opened — expiry unchanged.",
            notice);
    }
}
