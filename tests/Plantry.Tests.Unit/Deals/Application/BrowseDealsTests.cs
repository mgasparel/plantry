using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Application;

/// <summary>
/// L2 tests for <see cref="BrowseDeals"/> (P5-7 / DJ3). Proves the clock-driven partition — active
/// (Confirmed ∧ in-window, DD7) vs pending (Pending ∧ today ≤ valid_to, DD14) against a supplied clock —
/// surfaces the auto-matched marker, and resolves product + store names via batch reads (no N+1). Nothing
/// is stored: the same deals partition differently as the clock moves.
/// </summary>
public sealed class BrowseDealsTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Store = Guid.NewGuid();

    private readonly FakeDealRepository _deals = new();
    private readonly FakeCatalogProductReader _products = new();
    private readonly FakeCatalogStoreReader _stores = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero));

    private BrowseDeals Sut => new(_deals, _products, _stores, _clock);

    public BrowseDealsTests()
    {
        _stores.Names[Store] = "FreshCo";
    }

    private Deal Stage(string rawName, DateOnly from, DateOnly to, Guid? suggested = null)
    {
        var window = ValidityWindow.Create(from, to).Value;
        var raw = new RawDeal(rawName, null, null, 3.99m, null, null, "Save $1", window);
        var proposal = suggested is { } s
            ? new MatchProposal(s, MatchConfidence.Low, "maybe")
            : MatchProposal.Unmatched();
        var deal = Deal.Stage(Household, FlyerImportId.New(), Store, raw, DealNormalizer.Normalize(rawName), proposal, _clock);
        _deals.Items.Add(deal);
        return deal;
    }

    [Fact(DisplayName = "Active = Confirmed ∧ in-window; a confirmed deal outside its window is not active (DD7)")]
    public async Task Active_Partition_Respects_Window()
    {
        var product = Guid.NewGuid();
        _products.Products[product] = new DealProductInfo(product, "Whole Milk", "Dairy");

        var inWindow = Stage("Milk", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7));
        inWindow.Confirm(product, Guid.NewGuid(), _clock);

        var expired = Stage("Old Bread", new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31));
        expired.Confirm(product, Guid.NewGuid(), _clock);

        var future = Stage("Future Eggs", new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 7));
        future.Confirm(product, Guid.NewGuid(), _clock);

        var board = await Sut.BrowseAsync();

        Assert.Single(board.Active);
        Assert.Equal(inWindow.Id, board.Active[0].DealId);
        Assert.Empty(board.Pending);
    }

    [Fact(DisplayName = "Pending = Pending ∧ today ≤ valid_to; an expired-unreviewed deal drops off the queue (DD14)")]
    public async Task Pending_Partition_Drops_Expired()
    {
        var live = Stage("New Cheese", new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 9));
        Stage("Stale Yogurt", new DateOnly(2025, 12, 20), new DateOnly(2025, 12, 28)); // expired, unreviewed

        var board = await Sut.BrowseAsync();

        Assert.Empty(board.Active);
        Assert.Single(board.Pending);
        Assert.Equal(1, board.PendingCount);
        Assert.Equal(live.Id, board.Pending[0].DealId);
    }

    [Fact(DisplayName = "A Rejected deal appears in neither partition")]
    public async Task Rejected_Excluded()
    {
        var rejected = Stage("Nope", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7));
        rejected.Reject(Guid.NewGuid(), _clock);

        var board = await Sut.BrowseAsync();

        Assert.Empty(board.Active);
        Assert.Empty(board.Pending);
        Assert.True(board.IsEmpty);
    }

    [Fact(DisplayName = "auto_matched is surfaced on an auto-confirmed active deal (DL-O3)")]
    public async Task AutoMatched_Surfaced()
    {
        var product = Guid.NewGuid();
        _products.Products[product] = new DealProductInfo(product, "Butter", "Dairy");

        var auto = Stage("Butter 454g", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7));
        auto.AutoConfirm(product, _clock);

        var manual = Stage("Bacon", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7));
        manual.Confirm(product, Guid.NewGuid(), _clock);

        var board = await Sut.BrowseAsync();

        var autoView = board.Active.Single(d => d.DealId == auto.Id);
        var manualView = board.Active.Single(d => d.DealId == manual.Id);
        Assert.True(autoView.AutoMatched);
        Assert.False(manualView.AutoMatched);
    }

    [Fact(DisplayName = "Product + store names resolved via a single batch call — no N+1 across the page")]
    public async Task Names_Resolved_In_One_Batch()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        _products.Products[p1] = new DealProductInfo(p1, "Whole Milk", "Dairy");
        _products.Products[p2] = new DealProductInfo(p2, "Cheddar", "Dairy");

        var d1 = Stage("Milk 2L", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7));
        d1.Confirm(p1, Guid.NewGuid(), _clock);
        var d2 = Stage("Cheddar 400g", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7));
        d2.Confirm(p2, Guid.NewGuid(), _clock);

        var board = await Sut.BrowseAsync();

        // Exactly one batch resolve for the whole page (no per-deal call).
        Assert.Single(_products.ForProductsCalls);
        Assert.All(board.Active, v => Assert.Equal("FreshCo", v.StoreName));
        Assert.Contains(board.Active, v => v.ProductName == "Whole Milk" && v.CategoryName == "Dairy");
        Assert.Contains(board.Active, v => v.ProductName == "Cheddar");
    }

    [Fact(DisplayName = "A pending deal has no resolved product — DisplayName falls back to the raw flyer name")]
    public async Task Pending_DisplayName_Falls_Back_To_Raw()
    {
        Stage("Fresh Salmon Fillet", new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 9), suggested: Guid.NewGuid());

        var board = await Sut.BrowseAsync();

        var pending = Assert.Single(board.Pending);
        Assert.Null(pending.ProductName);
        Assert.Equal("Fresh Salmon Fillet", pending.DisplayName);
    }

    [Fact(DisplayName = "The same deals repartition as the clock moves — nothing about 'active' is stored")]
    public async Task Partition_Is_Clock_Driven()
    {
        var product = Guid.NewGuid();
        _products.Products[product] = new DealProductInfo(product, "Ham", null);

        var deal = Stage("Ham", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7));
        deal.Confirm(product, Guid.NewGuid(), _clock);

        // Today = 2026-01-04 → in window → active.
        Assert.Single((await Sut.BrowseAsync()).Active);

        // Advance past valid_to → the very same confirmed deal is no longer active.
        _clock.Advance(TimeSpan.FromDays(10));
        Assert.Empty((await Sut.BrowseAsync()).Active);
    }

    [Fact(DisplayName = "No deals → empty board (drives the subscribe-inviting empty state)")]
    public async Task Empty_When_No_Deals()
    {
        var board = await Sut.BrowseAsync();

        Assert.True(board.IsEmpty);
        Assert.Empty(board.Active);
        Assert.Empty(board.Pending);
    }
}
