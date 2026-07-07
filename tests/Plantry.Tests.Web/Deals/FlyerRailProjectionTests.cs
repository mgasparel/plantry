using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Web.Pages.Deals;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// L4 tests for the flyer-rail projection (q9zr.3) over <b>synthetic</b> view models. The live dev seed has
/// a single store/flyer, so the &gt;3-flyer compact density and the pill ordering (soonest-expiring first,
/// done last) can only be exercised here (epic known-limitation note, q9zr.14). Covers: server-side flyer
/// grouping counts/order, the density switch at 4 flyers, default active-flyer selection, done-last ordering,
/// the compact summary line, and the expiry-urgency boundary at 2 days.
/// </summary>
public sealed class FlyerRailProjectionTests
{
    private static readonly DateOnly Today = new(2026, 7, 7);

    private static DealReviewView View(Guid storeId, string storeName, DateOnly validFrom, DateOnly validTo) =>
        new(
            DealId.New(), storeId, storeName, "SOME DEAL", Brand: null, SaleStory: null,
            Price: 4.99m, Quantity: null, validFrom, validTo,
            MatchConfidence.None, Reasoning: null, SuggestedProductId: null, SuggestedProductName: null,
            DealStatus.Pending, AutoMatched: false);

    private static FlyerBlock Block(string store, int expiresInDays, int pendingCount)
    {
        var storeId = Guid.NewGuid();
        var validTo = Today.AddDays(expiresInDays);
        var validFrom = validTo.AddDays(-7);
        var deals = Enumerable.Range(0, pendingCount)
            .Select(_ => View(storeId, store, validFrom, validTo))
            .ToList();
        return new FlyerBlock(storeId, store, validFrom, validTo, expiresInDays, deals);
    }

    // ── Grouping (ReviewDeals.GroupIntoFlyers) ────────────────────────────────────

    [Fact(DisplayName = "GroupIntoFlyers groups by (store, window), counts each flyer, and orders soonest-expiring first")]
    public void Groups_By_Store_And_Window_Soonest_First()
    {
        var metro = Guid.NewGuid();
        var sobeys = Guid.NewGuid();
        var sobeysWindowA = (from: Today.AddDays(-3), to: Today.AddDays(3));
        var sobeysWindowB = (from: Today.AddDays(-1), to: Today.AddDays(6)); // a second overlapping flyer

        var pending = new List<DealReviewView>
        {
            View(sobeys, "Sobeys", sobeysWindowA.from, sobeysWindowA.to),
            View(sobeys, "Sobeys", sobeysWindowA.from, sobeysWindowA.to),
            View(metro, "Metro", Today.AddDays(-2), Today.AddDays(1)),   // expires soonest (1 day)
            View(sobeys, "Sobeys", sobeysWindowB.from, sobeysWindowB.to),
        };

        var flyers = ReviewDeals.GroupIntoFlyers(pending, Today);

        // Three flyer blocks: Metro (1 window) + Sobeys (2 distinct windows).
        Assert.Equal(3, flyers.Count);
        // Soonest-expiring first: Metro (1d) → Sobeys window A (3d) → Sobeys window B (6d).
        Assert.Equal(new[] { "Metro", "Sobeys", "Sobeys" }, flyers.Select(f => f.StoreName).ToArray());
        Assert.Equal(new[] { 1, 3, 6 }, flyers.Select(f => f.ExpiresInDays).ToArray());
        // Counts per flyer.
        Assert.Equal(1, flyers[0].PendingCount);
        Assert.Equal(2, flyers[1].PendingCount);
        Assert.Equal(1, flyers[2].PendingCount);
    }

    [Fact(DisplayName = "GroupIntoFlyers computes ExpiresInDays from ValidTo − today (never negative)")]
    public void Computes_Expiry_Countdown_From_Clock()
    {
        var store = Guid.NewGuid();
        var pending = new List<DealReviewView>
        {
            View(store, "Metro", Today.AddDays(-5), Today),          // expires today → 0
            View(store, "Sobeys", Today.AddDays(-1), Today.AddDays(2)),
        };

        var flyers = ReviewDeals.GroupIntoFlyers(pending, Today);

        Assert.Equal(0, flyers.Single(f => f.StoreName == "Metro").ExpiresInDays);
        Assert.Equal(2, flyers.Single(f => f.StoreName == "Sobeys").ExpiresInDays);
    }

    // ── Density switch (FlyerRail.IsCompact) ──────────────────────────────────────

    [Fact(DisplayName = "Rail stays big-chip density at 3 flyers")]
    public void Density_Big_Chips_At_Three_Flyers()
    {
        var rail = FlyerRail.Build(
            [Block("A", 1, 5), Block("B", 3, 5), Block("C", 5, 5)], activeKey: null);

        Assert.Equal(3, rail.Chapters.Count);
        Assert.False(rail.IsCompact);
    }

    [Fact(DisplayName = "Rail switches to compact pills at 4 flyers")]
    public void Density_Switches_To_Compact_At_Four_Flyers()
    {
        var rail = FlyerRail.Build(
            [Block("A", 1, 5), Block("B", 3, 5), Block("C", 5, 5), Block("D", 7, 5)], activeKey: null);

        Assert.Equal(4, rail.Chapters.Count);
        Assert.True(rail.IsCompact);
    }

    // ── Pill ordering: soonest-expiring first, done last ──────────────────────────

    [Fact(DisplayName = "Rail orders chapters soonest-expiring first with fully-reviewed (done) flyers last")]
    public void Orders_Soonest_First_Done_Last()
    {
        var blocks = new List<FlyerBlock>
        {
            Block("Loblaws", 5, 9),
            Block("NoFrills", 2, 0),   // done (0 pending) — must sort last despite the nearest expiry
            Block("Metro", 1, 6),
            Block("Sobeys", 3, 14),
        };

        var rail = FlyerRail.Build(blocks, activeKey: null);

        Assert.Equal(
            new[] { "Metro", "Sobeys", "Loblaws", "NoFrills" },
            rail.Chapters.Select(c => c.StoreName).ToArray());
        Assert.True(rail.Chapters.Last().IsDone);
    }

    // ── Default active flyer = soonest-expiring pending ───────────────────────────

    [Fact(DisplayName = "ResolveActiveKey defaults to the soonest-expiring flyer with pending work")]
    public void Default_Active_Is_Soonest_Expiring_Pending()
    {
        var done = Block("NoFrills", 1, 0);   // nearest expiry but nothing pending — never the default
        var soonestPending = Block("Metro", 2, 6);
        var later = Block("Sobeys", 5, 14);
        var blocks = new List<FlyerBlock> { done, later, soonestPending };

        var key = FlyerRail.ResolveActiveKey(blocks, requested: null);

        Assert.Equal(soonestPending.Key, key);
    }

    [Fact(DisplayName = "ResolveActiveKey honours a requested flyer that still has pending work")]
    public void Requested_Pending_Flyer_Is_Honoured()
    {
        var metro = Block("Metro", 2, 6);
        var sobeys = Block("Sobeys", 5, 14);
        var blocks = new List<FlyerBlock> { metro, sobeys };

        var key = FlyerRail.ResolveActiveKey(blocks, requested: sobeys.Key);

        Assert.Equal(sobeys.Key, key);
    }

    [Fact(DisplayName = "ResolveActiveKey falls back to the default when the requested flyer is finished/stale")]
    public void Requested_Finished_Flyer_Falls_Back_To_Default()
    {
        var finished = Block("Metro", 1, 0);   // requested, but nothing pending left → hand off
        var nextPending = Block("Sobeys", 3, 14);
        var blocks = new List<FlyerBlock> { finished, nextPending };

        var key = FlyerRail.ResolveActiveKey(blocks, requested: finished.Key);

        Assert.Equal(nextPending.Key, key);
    }

    // ── Expiry-urgency boundary at 2 days ─────────────────────────────────────────

    [Theory(DisplayName = "A pending flyer is urgent at or under 2 days left, not beyond")]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(6, false)]
    public void Urgency_Boundary_At_Two_Days(int expiresInDays, bool expectedUrgent)
    {
        var rail = FlyerRail.Build([Block("Metro", expiresInDays, 5)], activeKey: null);

        Assert.Equal(expectedUrgent, rail.Chapters.Single().IsUrgent);
    }

    [Fact(DisplayName = "A done flyer is never urgent, even at 0 days left")]
    public void Done_Flyer_Is_Never_Urgent()
    {
        var rail = FlyerRail.Build([Block("NoFrills", 0, 0)], activeKey: null);

        var chapter = rail.Chapters.Single();
        Assert.True(chapter.IsDone);
        Assert.False(chapter.IsUrgent);
    }

    // ── Compact summary line ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Compact rail summary counts waiting flyers/deals and phrases the closest expiry")]
    public void Compact_Summary_Rolls_Up_Waiting_Work()
    {
        var blocks = new List<FlyerBlock>
        {
            Block("Metro", 1, 6),
            Block("Sobeys", 3, 14),
            Block("Loblaws", 5, 9),
            Block("NoFrills", 2, 0),   // done — excluded from the waiting roll-up
        };

        var rail = FlyerRail.Build(blocks, activeKey: null);

        Assert.True(rail.IsCompact);
        Assert.Equal(3, rail.WaitingCount);            // three flyers still have work
        Assert.Equal(6 + 14 + 9, rail.WaitingDeals);   // 29 pending deals across them
        Assert.Equal(1, rail.SoonestExpiryDays);
        Assert.Equal("tomorrow", rail.SoonestExpiryLabel);
    }

    [Theory(DisplayName = "Soonest-expiry label reads today / tomorrow / in N days")]
    [InlineData(0, "today")]
    [InlineData(1, "tomorrow")]
    [InlineData(4, "in 4 days")]
    public void Soonest_Expiry_Label_Phrasing(int soonest, string expected)
    {
        var rail = FlyerRail.Build(
            [Block("A", soonest, 3), Block("B", soonest + 2, 3),
             Block("C", soonest + 4, 3), Block("D", soonest + 6, 3)],
            activeKey: null);

        Assert.True(rail.IsCompact);
        Assert.Equal(expected, rail.SoonestExpiryLabel);
    }
}
