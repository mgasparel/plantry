using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Unit.Market.Deals;
using Xunit;

namespace Plantry.Tests.Unit.Market.Deals.Application;

/// <summary>
/// L2 tests for <see cref="ConfirmDeal"/> — the DJ4 commit seam (deals-domain-model §7). Over fake ports:
/// a confirm writes exactly one deal-sourced observation, upserts the match memory, and links the deal;
/// a correct supersedes (new observation row, memory repointed, committed id updated, prior row retained);
/// a mid-confirm failure is resumable without double-writing; a past-window confirm still backfills; and
/// the auto-confirm path carries no reviewer. Observations are asserted to write to Pricing (source=deal),
/// never a separate table (ADR-010 / P5-P).
/// </summary>
public sealed class ConfirmDealTests
{
    private readonly HouseholdId _household = HouseholdId.New();
    private readonly Guid _store = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _productA = Guid.NewGuid();
    private readonly Guid _productB = Guid.NewGuid();

    private static ValidityWindow Window(int year = 2026) =>
        ValidityWindow.Create(new DateOnly(year, 1, 1), new DateOnly(year, 1, 7)).Value;

    private Deal StageDeal(ValidityWindow? window = null)
    {
        var raw = new RawDeal("Whole Milk 2L", "Dairyland", "2L", 3.99m, 2m, Guid.NewGuid(), "Save $1", window ?? Window());
        return Deal.Stage(
            _household, FlyerImportId.New(), _store, raw,
            DealNormalizer.Normalize("Whole Milk 2L"),
            MatchProposal.Unmatched(),
            new TestClock());
    }

    /// <summary>A deal with no advertised pack size (null quantity/unitId) — the shape that exercises the
    /// (1, Guid.Empty) mapping in ConfirmDeal's <c>RecordDealObservationAsync</c> helper.</summary>
    private Deal StageDealWithNoPackSize()
    {
        var raw = new RawDeal(
            "Mystery Item", Brand: null, Size: null, 1.99m, Quantity: null, UnitId: null, "Save $1", Window());
        return Deal.Stage(
            _household, FlyerImportId.New(), _store, raw,
            DealNormalizer.Normalize("Mystery Item"),
            MatchProposal.Unmatched(),
            new TestClock());
    }

    private ConfirmDeal Service(
        FakeDealRepository deals, FakeDealMatchMemoryRepository memories,
        FakeCatalogProductReader products, FakePriceObservationRepository observations,
        TestClock clock, Guid? household = null) =>
        new(deals, memories, products, observations, new FakeUnitPriceCalculator(1m), clock,
            new FakeTenantContext(household ?? _household.Value), NullLogger<ConfirmDeal>.Instance,
            NullLogger<RecordObservationCommand>.Instance);

    private (FakeDealRepository deals, FakeDealMatchMemoryRepository memories,
        FakeCatalogProductReader products, FakePriceObservationRepository observations) Ports(Deal deal)
    {
        var deals = new FakeDealRepository();
        deals.Items.Add(deal);
        return (deals, new FakeDealMatchMemoryRepository(), new FakeCatalogProductReader(), new FakePriceObservationRepository());
    }

    [Fact(DisplayName = "Confirm writes exactly one deal observation, upserts memory, and links it (DD2)")]
    public async Task Confirm_WritesObservation_UpsertsMemory_Links()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);

        var result = await Service(deals, memories, products, observations, clock)
            .ConfirmAsync(deal.Id, _productA, _user);

        Assert.True(result.IsSuccess);

        // Exactly one observation, source=deal shape: store id, window, deal id, reviewer all projected.
        var obs = Assert.Single(observations.Items);
        Assert.Equal(_productA, obs.ProductId);
        Assert.Equal(3.99m, obs.Price);
        Assert.Equal(_store, obs.StoreId);
        Assert.Equal(deal.Id.Value, obs.SourceRef);
        Assert.Equal(new DateOnly(2026, 1, 1), obs.ValidFrom);
        Assert.Equal(new DateOnly(2026, 1, 7), obs.ValidTo);
        Assert.Equal(_user, obs.UserId);

        // Memory upserted (positive), keyed on the normalized name.
        var memory = Assert.Single(memories.Items);
        Assert.Equal("whole milk", memory.NormalizedName);
        Assert.Equal(_productA, memory.ProductId);
        Assert.Equal(_store, memory.StoreId);

        // Deal linked + confirmed.
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(obs.Id.Value, deal.CommittedPriceObservationId);
        Assert.Equal(_user, deal.ReviewedByUserId);
        Assert.Contains(deal.DomainEvents, e => e is DealConfirmedEvent);
    }

    [Fact(DisplayName = "Correct on a confirmed deal supersedes: new observation row, memory repointed, committed id updated, prior row retained")]
    public async Task Correct_Supersedes()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);
        var service = Service(deals, memories, products, observations, clock);

        await service.ConfirmAsync(deal.Id, _productA, _user);
        var firstObservationId = deal.CommittedPriceObservationId;

        var result = await service.CorrectAsync(deal.Id, _productB, _user);

        Assert.True(result.IsSuccess);

        // Two observation rows total — the prior is retained (append-only), a new one written for productB.
        Assert.Equal(2, observations.Items.Count);
        Assert.Contains(observations.Items, o => o.ProductId == _productA);
        var superseding = Assert.Single(observations.Items, o => o.ProductId == _productB);

        // Committed id updated to the new row.
        Assert.Equal(superseding.Id.Value, deal.CommittedPriceObservationId);
        Assert.NotEqual(firstObservationId, deal.CommittedPriceObservationId);

        // Memory repointed to the corrected product — still one row.
        var memory = Assert.Single(memories.Items);
        Assert.Equal(_productB, memory.ProductId);
        Assert.Equal(_productB, deal.ProductId);
        Assert.False(deal.AutoMatched);
    }

    [Fact(DisplayName = "Mid-confirm failure is resumable — re-drive links only the missing observation without double-writing")]
    public async Task MidConfirm_Failure_IsResumable()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);
        observations.ThrowOnAdd = 1; // the observation write blows up after the state flip is saved

        var firstRun = await Service(deals, memories, products, observations, clock)
            .ConfirmAsync(deal.Id, _productA, _user);

        Assert.True(firstRun.IsFailure);
        Assert.Equal(ConfirmDeal.CommitFailed, firstRun.Error);
        Assert.Equal(DealStatus.Confirmed, deal.Status);         // state flip committed
        Assert.Null(deal.CommittedPriceObservationId);           // observation not linked
        Assert.Empty(observations.Items);                 // nothing written on the failed run

        // Re-drive: state flip is skipped (already confirmed), memory upsert is idempotent, only the
        // observation is written + linked this time.
        var secondRun = await Service(deals, memories, products, observations, clock)
            .ConfirmAsync(deal.Id, _productA, _user);

        Assert.True(secondRun.IsSuccess);
        var obs = Assert.Single(observations.Items);      // exactly one — never double-written
        Assert.Equal(obs.Id.Value, deal.CommittedPriceObservationId);
        Assert.Single(memories.Items);                           // memory not duplicated
    }

    [Fact(DisplayName = "Re-driving an already-linked confirm is a no-op — no second observation")]
    public async Task Redrive_Of_Linked_Confirm_DoesNotRewrite()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);
        var service = Service(deals, memories, products, observations, clock);

        await service.ConfirmAsync(deal.Id, _productA, _user);
        await service.ConfirmAsync(deal.Id, _productA, _user); // re-drive of a fully-committed confirm

        Assert.Single(observations.Items);
        Assert.Single(memories.Items);
    }

    [Fact(DisplayName = "Past-window confirm still writes the observation (backfill, DD14)")]
    public async Task PastWindow_Confirm_StillWrites()
    {
        // Clock is 2026-07-01; the window closed 2025-01-07 (well in the past).
        var clock = new TestClock();
        var deal = StageDeal(Window(2025));
        var (deals, memories, products, observations) = Ports(deal);

        var result = await Service(deals, memories, products, observations, clock)
            .ConfirmAsync(deal.Id, _productA, _user);

        Assert.True(result.IsSuccess);
        var obs = Assert.Single(observations.Items);
        Assert.Equal(new DateOnly(2025, 1, 1), obs.ValidFrom);
        Assert.Equal(new DateOnly(2025, 1, 7), obs.ValidTo);
    }

    [Fact(DisplayName = "AutoConfirm carries no reviewer on the deal, memory, or observation (P5-6 path)")]
    public async Task AutoConfirm_HasNoReviewer()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);

        var result = await Service(deals, memories, products, observations, clock)
            .AutoConfirmAsync(deal.Id, _productA);

        Assert.True(result.IsSuccess);
        Assert.True(deal.AutoMatched);
        Assert.Null(deal.ReviewedByUserId);
        Assert.Equal(Guid.Empty, Assert.Single(observations.Items).UserId);
        Assert.Null(Assert.Single(memories.Items).LastConfirmedByUserId);
    }

    [Fact(DisplayName = "Confirm to a product that is not in the catalog is rejected before any write")]
    public async Task Confirm_UnknownProduct_Rejected()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);
        products.Exists = false;

        var result = await Service(deals, memories, products, observations, clock)
            .ConfirmAsync(deal.Id, _productA, _user);

        Assert.True(result.IsFailure);
        Assert.Equal(ConfirmDeal.UnknownProduct, result.Error);
        Assert.Equal(DealStatus.Pending, deal.Status);
        Assert.Empty(observations.Items);
        Assert.Empty(memories.Items);
    }

    [Fact(DisplayName = "Confirm fails Unauthorized when there is no household in context")]
    public async Task Confirm_NoHousehold_Unauthorized()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);

        var service = new ConfirmDeal(
            deals, memories, products, observations, new FakeUnitPriceCalculator(1m), clock,
            new FakeTenantContext(null), NullLogger<ConfirmDeal>.Instance,
            NullLogger<RecordObservationCommand>.Instance);
        var result = await service.ConfirmAsync(deal.Id, _productA, _user);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
        Assert.Empty(observations.Items);
    }

    [Fact(DisplayName = "Confirm fails NotFound when the deal does not exist")]
    public async Task Confirm_DealNotFound()
    {
        var clock = new TestClock();
        var deals = new FakeDealRepository(); // deal not added
        var result = await Service(deals, new(), new(), new(), clock)
            .ConfirmAsync(DealId.New(), _productA, _user);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
    }

    // ── RecordDealObservationAsync mapping semantics (plantry-riqy) ──
    // Formerly covered at the RecordDealObservationAdapter seam; that adapter is gone (the Deals→Pricing
    // observation write is now an intra-context call, ADR-024), so its mapping-semantics coverage — null
    // quantity/unitId defaults, missing-reviewer mapping, and hard-failure handling — now lives here at
    // the ConfirmDeal level, over the same fake IPriceObservationRepository/IUnitPriceCalculator ports.

    [Fact(DisplayName = "A deal with no advertised pack size maps to (quantity=1, unitId=Guid.Empty) on the written observation")]
    public async Task Confirm_NoPackSize_MapsToDefaultQuantityAndEmptyUnit()
    {
        var clock = new TestClock();
        var deal = StageDealWithNoPackSize();
        var (deals, memories, products, observations) = Ports(deal);

        var result = await Service(deals, memories, products, observations, clock)
            .ConfirmAsync(deal.Id, _productA, _user);

        Assert.True(result.IsSuccess);
        var obs = Assert.Single(observations.Items);
        Assert.Equal(1m, obs.Quantity);
        Assert.Equal(Guid.Empty, obs.UnitId);
    }

    [Fact(DisplayName = "A missing reviewer (memory auto-confirm) maps to Guid.Empty on the written observation, never null")]
    public async Task AutoConfirm_MissingReviewer_MapsToEmptyGuidOnObservation()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);

        var result = await Service(deals, memories, products, observations, clock)
            .AutoConfirmAsync(deal.Id, _productA);

        Assert.True(result.IsSuccess);
        Assert.Equal(Guid.Empty, Assert.Single(observations.Items).UserId);
    }

    [Fact(DisplayName = "A hard failure recording the deal observation aborts the confirm as CommitFailed (resumable), never a raw throw to the caller")]
    public async Task Confirm_ObservationWriteHardFailure_MapsToCommitFailed()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var (deals, memories, products, observations) = Ports(deal);
        observations.ThrowOnAdd = 1; // simulates the former adapter's "hard command failure" path

        var result = await Service(deals, memories, products, observations, clock)
            .ConfirmAsync(deal.Id, _productA, _user);

        Assert.True(result.IsFailure);
        Assert.Equal(ConfirmDeal.CommitFailed, result.Error);
        Assert.Empty(observations.Items);
    }
}
