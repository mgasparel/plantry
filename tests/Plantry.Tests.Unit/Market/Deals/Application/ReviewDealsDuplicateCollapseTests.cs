using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.Market.Deals.Application;

/// <summary>
/// L2 tests for the review projection's duplicate-flyer-crop collapse (plantry-g1u9): Flipp's page-image-based
/// flyer feed sometimes detects/crops the same advertised deal several times, so ingestion mirrors the feed
/// 1:1 into several byte-identical <see cref="Deal"/> rows with no product-identity signal to merge them.
/// <see cref="ReviewDeals"/> collapses those repeats to one representative per (store, validity window,
/// normalized name, price, brand, size) group before rendering, so the reviewer never sees the same advertised
/// deal as several separate cards.
/// </summary>
public sealed class ReviewDealsDuplicateCollapseTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid FoodBasics = Guid.NewGuid();
    private static readonly Guid Metro = Guid.NewGuid();

    private readonly FakeDealRepository _deals = new();
    private readonly FakeCatalogProductReader _products = new();
    private readonly FakeCatalogStoreReader _stores = new();
    private readonly FakeFlyerImportRepository _flyerImports = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

    private ReviewDeals Sut => new(
        _deals, _products, _stores, _flyerImports, _clock,
        new PricingQueries(new FakePriceObservationRepository()),
        new FakePurchaseFrequencyReader(),
        new FakeUnitPriceCalculator(null));

    private static readonly DateOnly Today = new(2026, 8, 6);
    private static readonly DateOnly ValidFrom = Today.AddDays(-1);
    private static readonly DateOnly ValidTo = Today.AddDays(6);

    public ReviewDealsDuplicateCollapseTests()
    {
        _stores.Names[FoodBasics] = "Food Basics";
        _stores.Names[Metro] = "Metro";
    }

    private Deal Stage(
        Guid storeId, string rawName, decimal price, string? brand = null, string? size = null,
        DateOnly? from = null, DateOnly? to = null, DateTimeOffset? createdAt = null,
        decimal? quantity = null, string? saleStory = null)
    {
        var window = ValidityWindow.Create(from ?? ValidFrom, to ?? ValidTo).Value;
        var raw = new RawDeal(rawName, brand, size, price, quantity, null, saleStory, window);
        var stageClock = createdAt is { } at ? new TestClock(at) : _clock;
        var deal = Deal.Stage(
            Household, FlyerImportId.New(), storeId, raw, DealNormalizer.Normalize(rawName),
            MatchProposal.Unmatched(), stageClock);
        _deals.Items.Add(deal);
        return deal;
    }

    [Fact(DisplayName =
        "Four flyer_item crops identical on every advertised field collapse to one card (the confirmed live bug: Food Basics 'RED MANGOES...' x4)")]
    public async Task Collapses_Byte_Identical_Duplicate_Crops_To_One_Card()
    {
        Stage(FoodBasics, "RED MANGOES OR HONEY ATAULFO MANGOES", 1.49m);
        Stage(FoodBasics, "RED MANGOES OR HONEY ATAULFO MANGOES", 1.49m);
        Stage(FoodBasics, "RED MANGOES OR HONEY ATAULFO MANGOES", 1.49m);
        Stage(FoodBasics, "RED MANGOES OR HONEY ATAULFO MANGOES", 1.49m);

        var projection = await Sut.ProjectPendingQueueAsync();

        var flyer = Assert.Single(projection.Flyers);
        var card = Assert.Single(flyer.Deals);
        Assert.Equal("RED MANGOES OR HONEY ATAULFO MANGOES", card.RawName);
        Assert.Equal(1.49m, card.Price);
        Assert.Equal(_deals.Items.Select(d => d.Id.Value).Where(id => id != card.DealId.Value).Order(), card.DuplicateDealIds!.Order());
    }

    [Fact(DisplayName = "Equal CreatedAt chooses the lowest DealId as representative")]
    public async Task Equal_CreatedAt_Uses_Lowest_Id_Tie_Breaker()
    {
        var createdAt = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var first = Stage(FoodBasics, "Tie", 1m, createdAt: createdAt);
        var second = Stage(FoodBasics, "Tie", 1m, createdAt: createdAt);
        var expected = new[] { first, second }.OrderBy(d => d.Id.Value).First();

        var cards = Assert.Single((await Sut.ProjectPendingQueueAsync()).Flyers).Deals;

        Assert.Equal(expected.Id, cards[0].DealId);
    }

    [Fact(DisplayName = "A non-duplicate card has no duplicate siblings")]
    public async Task NonDuplicate_Has_Empty_Sibling_List()
    {
        Stage(FoodBasics, "A", 1m);
        Stage(FoodBasics, "B", 1m);

        var cards = Assert.Single((await Sut.ProjectPendingQueueAsync()).Flyers).Deals;
        Assert.All(cards, card => Assert.Empty(card.DuplicateDealIds!));
    }

    [Fact(DisplayName = "FindAsync discovers pending siblings from a hidden crop")]
    public async Task FindAsync_Resolves_Hidden_Sibling_Group()
    {
        var first = Stage(FoodBasics, "A", 1m);
        var second = Stage(FoodBasics, "A", 1m);
        var view = await Sut.FindAsync(second.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Equal([first.Id.Value], view!.DuplicateDealIds);
    }

    [Fact(DisplayName = "FindAsync returns every other pending member from the representative")]
    public async Task FindAsync_Representative_Returns_All_Pending_Siblings()
    {
        var representative = Stage(FoodBasics, "A", 1m);
        var siblingB = Stage(FoodBasics, "A", 1m);
        var siblingC = Stage(FoodBasics, "A", 1m);
        var siblingD = Stage(FoodBasics, "A", 1m);

        var view = await Sut.FindAsync(representative.Id, includePurchaseContext: false);

        Assert.NotNull(view);
        Assert.Equal(new[] { siblingB.Id.Value, siblingC.Id.Value, siblingD.Id.Value }.Order(),
            view!.DuplicateDealIds!.Order());
    }

    [Fact(DisplayName = "FindAsync from a hidden crop returns the representative and remaining pending members")]
    public async Task FindAsync_Hidden_Target_Returns_Other_Pending_Members()
    {
        var representative = Stage(FoodBasics, "A", 1m);
        var hiddenTarget = Stage(FoodBasics, "A", 1m);
        var remainingB = Stage(FoodBasics, "A", 1m);
        var remainingC = Stage(FoodBasics, "A", 1m);

        var view = await Sut.FindAsync(hiddenTarget.Id, includePurchaseContext: false);

        Assert.NotNull(view);
        Assert.Equal(new[] { representative.Id.Value, remainingB.Id.Value, remainingC.Id.Value }.Order(),
            view!.DuplicateDealIds!.Order());
    }

    [Fact(DisplayName = "FindAsync excludes confirmed and rejected matches, retaining pending cleanup candidates")]
    public async Task FindAsync_Excludes_NonPending_Matches()
    {
        var target = Stage(FoodBasics, "A", 1m);
        var pending = Stage(FoodBasics, "A", 1m);
        var confirmed = Stage(FoodBasics, "A", 1m);
        var rejected = Stage(FoodBasics, "A", 1m);
        confirmed.Confirm(Guid.NewGuid(), Guid.NewGuid(), _clock);
        rejected.Reject(Guid.NewGuid(), _clock);

        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);

        Assert.NotNull(view);
        Assert.Equal(new[] { pending.Id.Value }, view!.DuplicateDealIds);
    }

    [Fact(DisplayName = "FindAsync on a confirmed target returns only pending matching siblings")]
    public async Task FindAsync_Confirmed_Target_Returns_Pending_Siblings_Only()
    {
        var confirmedTarget = Stage(FoodBasics, "A", 1m);
        var pending = Stage(FoodBasics, "A", 1m);
        var rejected = Stage(FoodBasics, "A", 1m);
        confirmedTarget.Confirm(Guid.NewGuid(), Guid.NewGuid(), _clock);
        rejected.Reject(Guid.NewGuid(), _clock);

        var view = await Sut.FindAsync(confirmedTarget.Id, includePurchaseContext: false);

        Assert.NotNull(view);
        Assert.Equal(new[] { pending.Id.Value }, view!.DuplicateDealIds);
    }

    [Fact(DisplayName = "FindAsync does not cross advertised identity boundaries")]
    public async Task FindAsync_Different_Identity_Has_No_Siblings()
    {
        var target = Stage(FoodBasics, "A", 1m);
        Stage(FoodBasics, "A", 2m);
        Stage(Metro, "A", 1m);
        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Empty(view!.DuplicateDealIds!);
    }

    [Fact(DisplayName = "FindAsync does not treat a validity start difference as a duplicate")]
    public async Task FindAsync_Different_ValidityStart_Has_No_Sibling()
    {
        var target = Stage(FoodBasics, "A", 1m);
        Stage(FoodBasics, "A", 1m, from: ValidFrom.AddDays(-1));
        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Empty(view!.DuplicateDealIds!);
    }

    [Fact(DisplayName = "FindAsync does not treat a validity end difference as a duplicate")]
    public async Task FindAsync_Different_ValidityEnd_Has_No_Sibling()
    {
        var target = Stage(FoodBasics, "A", 1m);
        Stage(FoodBasics, "A", 1m, to: ValidTo.AddDays(1));
        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Empty(view!.DuplicateDealIds!);
    }

    [Fact(DisplayName = "FindAsync does not treat a normalized name difference as a duplicate")]
    public async Task FindAsync_Different_NormalizedName_Has_No_Sibling()
    {
        var target = Stage(FoodBasics, "A", 1m);
        Stage(FoodBasics, rawName: "A  B", 1m);
        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Empty(view!.DuplicateDealIds!);
    }

    [Fact(DisplayName = "FindAsync does not treat a brand difference as a duplicate")]
    public async Task FindAsync_Different_Brand_Has_No_Sibling()
    {
        var target = Stage(FoodBasics, "A", 1m, brand: "Brand");
        Stage(FoodBasics, "A", 1m, brand: "Other Brand");
        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Empty(view!.DuplicateDealIds!);
    }

    [Fact(DisplayName = "FindAsync does not treat a size difference as a duplicate")]
    public async Task FindAsync_Different_Size_Has_No_Sibling()
    {
        var target = Stage(FoodBasics, "A", 1m, size: "500g");
        Stage(FoodBasics, "A", 1m, size: "1kg");
        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Empty(view!.DuplicateDealIds!);
    }

    [Fact(DisplayName = "FindAsync does not treat a sale story difference as a duplicate")]
    public async Task FindAsync_Different_SaleStory_Has_No_Sibling()
    {
        var target = Stage(FoodBasics, "A", 1m, saleStory: "Same");
        Stage(FoodBasics, "A", 1m, saleStory: "Different");
        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Empty(view!.DuplicateDealIds!);
    }

    [Fact(DisplayName = "FindAsync does not treat a quantity difference as a duplicate")]
    public async Task FindAsync_Different_Quantity_Has_No_Sibling()
    {
        var target = Stage(FoodBasics, "A", 1m, quantity: 1m);
        Stage(FoodBasics, "A", 1m, quantity: 2m);
        var view = await Sut.FindAsync(target.Id, includePurchaseContext: false);
        Assert.NotNull(view);
        Assert.Empty(view!.DuplicateDealIds!);
    }

    [Fact(DisplayName = "ListPendingAsync (the flat queue) collapses duplicates the same way as the flyer-chaptered projection")]
    public async Task ListPendingAsync_Also_Collapses_Duplicates()
    {
        Stage(FoodBasics, "SAME DEAL", 2.99m);
        Stage(FoodBasics, "SAME DEAL", 2.99m);

        var pending = await Sut.ListPendingAsync();

        Assert.Single(pending);
    }

    [Fact(DisplayName = "Duplicates differing in price stay separate cards — a different price is a different advertised deal")]
    public async Task Different_Price_Stays_Separate()
    {
        Stage(FoodBasics, "SAME DEAL", 2.99m);
        Stage(FoodBasics, "SAME DEAL", 3.49m);

        var projection = await Sut.ProjectPendingQueueAsync();

        var flyer = Assert.Single(projection.Flyers);
        Assert.Equal(2, flyer.Deals.Count);
    }

    [Fact(DisplayName = "Duplicates differing in brand stay separate cards")]
    public async Task Different_Brand_Stays_Separate()
    {
        Stage(FoodBasics, "SAME DEAL", 2.99m, brand: "Brand A");
        Stage(FoodBasics, "SAME DEAL", 2.99m, brand: "Brand B");

        var projection = await Sut.ProjectPendingQueueAsync();

        var flyer = Assert.Single(projection.Flyers);
        Assert.Equal(2, flyer.Deals.Count);
    }

    [Fact(DisplayName = "Duplicates differing in size stay separate cards")]
    public async Task Different_Size_Stays_Separate()
    {
        Stage(FoodBasics, "SAME DEAL", 2.99m, size: "500g");
        Stage(FoodBasics, "SAME DEAL", 2.99m, size: "1kg");

        var projection = await Sut.ProjectPendingQueueAsync();

        var flyer = Assert.Single(projection.Flyers);
        Assert.Equal(2, flyer.Deals.Count);
    }

    [Fact(DisplayName = "Duplicates differing in sale story stay separate cards — a different promo story is a different advertised deal")]
    public async Task Different_SaleStory_Stays_Separate()
    {
        Stage(FoodBasics, "SAME DEAL", 5.00m, saleStory: "$5.00");
        Stage(FoodBasics, "SAME DEAL", 5.00m, saleStory: "$5.00 — BUY 2 GET 1 FREE");

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Equal(2, Assert.Single(projection.Flyers).Deals.Count);
    }

    [Fact(DisplayName = "Duplicates differing in quantity stay separate cards — a different multi-buy quantity is a different advertised deal")]
    public async Task Different_Quantity_Stays_Separate()
    {
        Stage(FoodBasics, "SAME DEAL", 5.00m, quantity: 1m);
        Stage(FoodBasics, "SAME DEAL", 5.00m, quantity: 2m);

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Equal(2, Assert.Single(projection.Flyers).Deals.Count);
    }

    [Fact(DisplayName = "Identical advertised fields at two different stores are NOT collapsed together — each store's flyer keeps its own card")]
    public async Task Same_Content_Different_Store_Stays_Separate()
    {
        Stage(FoodBasics, "SAME DEAL", 2.99m);
        Stage(Metro, "SAME DEAL", 2.99m);

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Equal(2, projection.Flyers.Count);
        Assert.All(projection.Flyers, f => Assert.Single(f.Deals));
    }

    [Fact(DisplayName = "Identical advertised fields in two non-overlapping validity windows at the same store are NOT collapsed together")]
    public async Task Same_Content_Different_Window_Stays_Separate()
    {
        Stage(FoodBasics, "SAME DEAL", 2.99m, from: Today.AddDays(-1), to: Today.AddDays(6));
        Stage(FoodBasics, "SAME DEAL", 2.99m, from: Today.AddDays(-2), to: Today.AddDays(3));

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Equal(2, projection.Flyers.Count);
    }

    [Fact(DisplayName =
        "Collapsed duplicate crops do not count as reviewed — ReviewedCount stays 0 until something is actually confirmed")]
    public async Task Collapsed_Duplicates_Are_Not_Counted_As_Reviewed()
    {
        Stage(FoodBasics, "RED MANGOES OR HONEY ATAULFO MANGOES", 1.49m);
        Stage(FoodBasics, "RED MANGOES OR HONEY ATAULFO MANGOES", 1.49m);
        Stage(FoodBasics, "RED MANGOES OR HONEY ATAULFO MANGOES", 1.49m);
        Stage(FoodBasics, "RED MANGOES OR HONEY ATAULFO MANGOES", 1.49m);

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Equal(0, projection.ReviewedCount);
    }

    [Fact(DisplayName = "The collapsed representative is the oldest-created duplicate, deterministic across renders")]
    public async Task Representative_Is_Oldest_Created()
    {
        var older = Stage(
            FoodBasics, "SAME DEAL", 2.99m, createdAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        Stage(FoodBasics, "SAME DEAL", 2.99m, createdAt: new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));

        var projection = await Sut.ProjectPendingQueueAsync();

        var card = Assert.Single(Assert.Single(projection.Flyers).Deals);
        Assert.Equal(older.Id, card.DealId);
    }
}
