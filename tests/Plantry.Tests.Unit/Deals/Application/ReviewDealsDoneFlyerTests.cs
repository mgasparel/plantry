using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Application;

/// <summary>
/// L2 tests for the Confirm-finished done-chip projection (plantry-8f7v, option a):
/// <see cref="ReviewDeals.ProjectPendingQueueAsync"/> exposes a <b>separate</b>
/// <see cref="ReviewQueueProjection.DoneFlyers"/> list of in-window (store, window) groups that have 0 pending
/// and ≥1 Confirmed, projected as display-only chapters (PendingCount 0). Proves: a fully-confirmed flyer
/// surfaces as a done chip and leaves the pending set; a still-pending flyer never becomes a done chip; the
/// done set never perturbs the progress counts; a past-window done flyer drops off (DD14); and an all-rejected
/// flyer is invisible here (the known gap tracked in plantry-wmt7).
/// </summary>
public sealed class ReviewDealsDoneFlyerTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid FreshCo = Guid.NewGuid();
    private static readonly Guid NoFrills = Guid.NewGuid();
    private static readonly Guid Metro = Guid.NewGuid();

    private readonly FakeDealRepository _deals = new();
    private readonly FakeCatalogProductReader _products = new();
    private readonly FakeCatalogStoreReader _stores = new();
    private readonly FakeFlyerImportRepository _flyerImports = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));

    private ReviewDeals Sut => new(_deals, _products, _stores, _flyerImports, _clock);

    private static readonly DateOnly Today = new(2026, 7, 1);

    public ReviewDealsDoneFlyerTests()
    {
        _stores.Names[FreshCo] = "FreshCo";
        _stores.Names[NoFrills] = "No Frills";
        _stores.Names[Metro] = "Metro";
    }

    private Deal Stage(Guid storeId, DateOnly from, DateOnly to)
    {
        var window = ValidityWindow.Create(from, to).Value;
        var raw = new RawDeal("SOME DEAL", null, null, 3.99m, null, null, "Save $1", window);
        var deal = Deal.Stage(
            Household, FlyerImportId.New(), storeId, raw, DealNormalizer.Normalize("SOME DEAL"),
            MatchProposal.Unmatched(), _clock);
        _deals.Items.Add(deal);
        return deal;
    }

    private Deal StageConfirmed(Guid storeId, DateOnly from, DateOnly to)
    {
        var deal = Stage(storeId, from, to);
        deal.Confirm(Guid.NewGuid(), Guid.NewGuid(), _clock);
        return deal;
    }

    private Deal StageRejected(Guid storeId, DateOnly from, DateOnly to)
    {
        var deal = Stage(storeId, from, to);
        deal.Reject(Guid.NewGuid(), _clock);
        return deal;
    }

    private void SeedParsedImport(Guid storeId, string externalId, DateOnly from, DateOnly to)
    {
        var window = ValidityWindow.Create(from, to).Value;
        var import = FlyerImport.Start(Household, storeId, externalId, contentHash: null, window, "{}", _clock);
        import.MarkParsed(pendingCount: 1, _clock);
        _flyerImports.Items.Add(import);
    }

    [Fact(DisplayName = "A fully-confirmed in-window flyer becomes a done chip (0 pending) and leaves the pending set")]
    public async Task Confirm_Finished_Flyer_Is_A_Done_Chip_Not_A_Pending_Chapter()
    {
        StageConfirmed(FreshCo, Today.AddDays(-1), Today.AddDays(6));
        StageConfirmed(FreshCo, Today.AddDays(-1), Today.AddDays(6)); // same (store, window) → one done chapter

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Empty(projection.Flyers);                       // nothing pending → no pending chapter
        var done = Assert.Single(projection.DoneFlyers);
        Assert.Equal(FreshCo, done.StoreId);
        Assert.Equal("FreshCo", done.StoreName);
        Assert.Equal(0, done.PendingCount);                    // display-only, no pending deals
        Assert.Equal(6, done.ExpiresInDays);                   // countdown still computed from ValidTo
    }

    [Fact(DisplayName = "A still-pending flyer is a pending chapter and never a done chip, even alongside a finished one")]
    public async Task Pending_Flyer_Stays_Pending_While_A_Sibling_Is_Done()
    {
        // FreshCo: fully confirmed → done. NoFrills: one pending → stays a pending chapter.
        StageConfirmed(FreshCo, Today.AddDays(-1), Today.AddDays(6));
        Stage(NoFrills, Today.AddDays(-2), Today.AddDays(3));

        var projection = await Sut.ProjectPendingQueueAsync();

        var pending = Assert.Single(projection.Flyers);
        Assert.Equal(NoFrills, pending.StoreId);
        Assert.Equal(1, pending.PendingCount);

        var done = Assert.Single(projection.DoneFlyers);
        Assert.Equal(FreshCo, done.StoreId);

        // The two never collide: no store/window appears in both sets.
        Assert.DoesNotContain(projection.Flyers, f => f.Key == done.Key);
    }

    [Fact(DisplayName = "A flyer with any pending deal is a pending chapter — never a done chip — even with confirmed siblings")]
    public async Task Mixed_Pending_And_Confirmed_Flyer_Is_Not_Done()
    {
        var from = Today.AddDays(-1);
        var to = Today.AddDays(6);
        StageConfirmed(FreshCo, from, to);
        Stage(FreshCo, from, to);   // same (store, window), still pending → the whole chapter is pending

        var projection = await Sut.ProjectPendingQueueAsync();

        var pending = Assert.Single(projection.Flyers);
        Assert.Equal(FreshCo, pending.StoreId);
        Assert.Equal(1, pending.PendingCount);
        Assert.Empty(projection.DoneFlyers);
    }

    [Fact(DisplayName = "A done flyer past its validity window (today > ValidTo) drops off the rail (DD14)")]
    public async Task Past_Window_Done_Flyer_Drops_Off()
    {
        // In-window confirmed flyer → done chip. Past-window confirmed flyer (expired yesterday) → gone.
        StageConfirmed(FreshCo, Today.AddDays(-6), Today.AddDays(2));
        StageConfirmed(NoFrills, Today.AddDays(-10), Today.AddDays(-1));

        var projection = await Sut.ProjectPendingQueueAsync();

        var done = Assert.Single(projection.DoneFlyers);
        Assert.Equal(FreshCo, done.StoreId);
        Assert.DoesNotContain(projection.DoneFlyers, f => f.StoreId == NoFrills);
    }

    [Fact(DisplayName = "An all-rejected flyer is invisible — no done chip (Rejected not browsable; known gap plantry-wmt7)")]
    public async Task All_Rejected_Flyer_Does_Not_Appear_As_A_Done_Chip()
    {
        StageRejected(Metro, Today.AddDays(-1), Today.AddDays(5));
        StageRejected(Metro, Today.AddDays(-1), Today.AddDays(5));

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Empty(projection.Flyers);
        Assert.Empty(projection.DoneFlyers);   // documents the plantry-wmt7 gap: nothing surfaces for it
    }

    [Fact(DisplayName = "The done set never perturbs the progress counts — a confirmed deal is counted once, as reviewed")]
    public async Task Done_Chip_Does_Not_Change_Progress_Counts()
    {
        // One confirmed (done) + one pending, distinct windows. Progress = in-window Confirmed / in-window total.
        StageConfirmed(FreshCo, Today.AddDays(-1), Today.AddDays(6));
        Stage(NoFrills, Today.AddDays(-2), Today.AddDays(3));

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Equal(1, projection.ReviewedCount);   // the one Confirmed deal
        Assert.Equal(2, projection.TotalCount);       // Pending + Confirmed in window
        Assert.Single(projection.DoneFlyers);         // and it also drives exactly one done chip (no double count)
    }

    [Fact(DisplayName = "A done chip resolves its source-flyer FlyerExternalId via the same batch path as pending chapters")]
    public async Task Done_Chip_Resolves_Its_Flyer_External_Id()
    {
        StageConfirmed(FreshCo, Today.AddDays(-1), Today.AddDays(6));
        SeedParsedImport(FreshCo, "flipp-freshco-2026-07", Today.AddDays(-1), Today.AddDays(6));

        var projection = await Sut.ProjectPendingQueueAsync();

        var done = Assert.Single(projection.DoneFlyers);
        Assert.Equal("flipp-freshco-2026-07", done.FlyerExternalId);
    }
}
