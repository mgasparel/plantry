using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Web.Pages.Today;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L2 unit tests for cook-now pick selection logic (<see cref="IndexModel.SelectCookNowPicks"/>).
///
/// Tests covering the acceptance criteria of plantry-81g:
/// <list type="bullet">
///   <item>Fulfillment ordering — higher fulfillment pct comes first.</item>
///   <item>Expiring-ingredient favour — expiring picks surface above higher-fulfillment non-expiring.</item>
///   <item>Top-N cap — at most <see cref="IndexModel.CookNowPickCount"/> picks returned.</item>
///   <item>Empty input — empty list returns empty picks.</item>
///   <item>Fewer than N recipes — returns all without error.</item>
/// </list>
/// </summary>
public sealed class CookNowPicksSelectionTests
{
    // ── Fulfillment ordering ─────────────────────────────────────────────────

    [Fact(DisplayName = "SelectCookNowPicks — orders by FulfillmentPct descending when no expiring ingredient")]
    public void SelectCookNowPicks_OrdersByFulfillmentDescending()
    {
        var rows = new[]
        {
            MakeRow(id: 1, fulfillmentPct: 50, hasExpiring: false),
            MakeRow(id: 2, fulfillmentPct: 100, hasExpiring: false),
            MakeRow(id: 3, fulfillmentPct: 75, hasExpiring: false),
        };

        var picks = IndexModel.SelectCookNowPicks(rows, maxPicks: 3);

        Assert.Equal(3, picks.Count);
        Assert.Equal(100, picks[0].FulfillmentPct);
        Assert.Equal(75,  picks[1].FulfillmentPct);
        Assert.Equal(50,  picks[2].FulfillmentPct);
    }

    // ── Expiring-ingredient favour ────────────────────────────────────────────

    [Fact(DisplayName = "SelectCookNowPicks — expiring-ingredient recipe ranks above higher-fulfillment non-expiring")]
    public void SelectCookNowPicks_FavoursExpiringOverHigherFulfillment()
    {
        var rows = new[]
        {
            MakeRow(id: 1, fulfillmentPct: 100, hasExpiring: false),  // fully cookable, not expiring
            MakeRow(id: 2, fulfillmentPct: 60,  hasExpiring: true),   // lower pct but expiring — should be first
        };

        var picks = IndexModel.SelectCookNowPicks(rows, maxPicks: 3);

        Assert.Equal(2, picks.Count);
        Assert.True(picks[0].HasIngredientExpiringSoon, "Expiring pick should be first");
        Assert.Equal(60, picks[0].FulfillmentPct);
        Assert.False(picks[1].HasIngredientExpiringSoon);
        Assert.Equal(100, picks[1].FulfillmentPct);
    }

    [Fact(DisplayName = "SelectCookNowPicks — within expiring tier, sorts by FulfillmentPct descending")]
    public void SelectCookNowPicks_WithinExpiringTier_OrdersByFulfillment()
    {
        var rows = new[]
        {
            MakeRow(id: 1, fulfillmentPct: 60,  hasExpiring: true),
            MakeRow(id: 2, fulfillmentPct: 100, hasExpiring: true),
            MakeRow(id: 3, fulfillmentPct: 80,  hasExpiring: true),
        };

        var picks = IndexModel.SelectCookNowPicks(rows, maxPicks: 3);

        Assert.Equal(100, picks[0].FulfillmentPct);
        Assert.Equal(80,  picks[1].FulfillmentPct);
        Assert.Equal(60,  picks[2].FulfillmentPct);
    }

    // ── Top-N cap ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "SelectCookNowPicks — caps results at CookNowPickCount (3) when more recipes exist")]
    public void SelectCookNowPicks_CapsAtPickCount()
    {
        var rows = new[]
        {
            MakeRow(id: 1, fulfillmentPct: 100, hasExpiring: false),
            MakeRow(id: 2, fulfillmentPct: 90,  hasExpiring: false),
            MakeRow(id: 3, fulfillmentPct: 80,  hasExpiring: false),
            MakeRow(id: 4, fulfillmentPct: 70,  hasExpiring: false),
            MakeRow(id: 5, fulfillmentPct: 60,  hasExpiring: false),
        };

        var picks = IndexModel.SelectCookNowPicks(rows);

        Assert.Equal(IndexModel.CookNowPickCount, picks.Count);
        Assert.Equal(100, picks[0].FulfillmentPct);
        Assert.Equal(90,  picks[1].FulfillmentPct);
        Assert.Equal(80,  picks[2].FulfillmentPct);
    }

    // ── Empty input ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "SelectCookNowPicks — returns empty list when no recipes")]
    public void SelectCookNowPicks_EmptyInput_ReturnsEmpty()
    {
        var picks = IndexModel.SelectCookNowPicks([], maxPicks: 3);

        Assert.Empty(picks);
    }

    // ── Fewer than N ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "SelectCookNowPicks — returns all when fewer than CookNowPickCount recipes exist")]
    public void SelectCookNowPicks_FewerThanPickCount_ReturnsAll()
    {
        var rows = new[]
        {
            MakeRow(id: 1, fulfillmentPct: 80, hasExpiring: false),
            MakeRow(id: 2, fulfillmentPct: 40, hasExpiring: false),
        };

        var picks = IndexModel.SelectCookNowPicks(rows, maxPicks: 3);

        Assert.Equal(2, picks.Count);
    }

    // ── Mixed expiring + non-expiring, cap applied ────────────────────────────

    [Fact(DisplayName = "SelectCookNowPicks — expiring at top, then fulfillment-sorted non-expiring, capped at 3")]
    public void SelectCookNowPicks_MixedTiersWithCap_ReturnsTopThreeInOrder()
    {
        var rows = new[]
        {
            MakeRow(id: 1, fulfillmentPct: 100, hasExpiring: false),
            MakeRow(id: 2, fulfillmentPct: 90,  hasExpiring: false),
            MakeRow(id: 3, fulfillmentPct: 70,  hasExpiring: true),   // expiring — should be slot 1
            MakeRow(id: 4, fulfillmentPct: 50,  hasExpiring: false),
        };

        var picks = IndexModel.SelectCookNowPicks(rows, maxPicks: 3);

        // Slot 1: expiring recipe (pct=70)
        Assert.True(picks[0].HasIngredientExpiringSoon);
        Assert.Equal(70, picks[0].FulfillmentPct);

        // Slots 2 & 3: non-expiring by fulfillment desc
        Assert.Equal(100, picks[1].FulfillmentPct);
        Assert.Equal(90,  picks[2].FulfillmentPct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal <see cref="RecipeBrowseRow"/> with the fields exercised by
    /// <see cref="IndexModel.SelectCookNowPicks"/> set; all other fields are defaults.
    /// </summary>
    private static RecipeBrowseRow MakeRow(int id, int fulfillmentPct, bool hasExpiring) =>
        new(
            RecipeId: new Guid($"00000000-0000-0000-0000-{id:D12}"),
            Name: $"Recipe {id}",
            CookTimeMinutes: null,
            DefaultServings: 2,
            CreatedAt: DateTimeOffset.UtcNow,
            TagIds: [],
            FullyCookable: fulfillmentPct == 100,
            FulfillmentPct: fulfillmentPct,
            InStockCount: fulfillmentPct / 10,
            TotalIngredientCount: 10,
            MissingCount: (100 - fulfillmentPct) / 10,
            HasIngredientExpiringSoon: hasExpiring,
            CostPerServing: null,
            CostCompleteness: CostCompleteness.None,
            HasPhoto: false);
}
