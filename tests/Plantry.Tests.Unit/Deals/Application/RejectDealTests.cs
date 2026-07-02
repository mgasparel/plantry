using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Unit.Deals;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Application;

/// <summary>
/// L2 tests for <see cref="RejectDeal"/> — DJ4 reject (deals-domain-model §7): flips a deal to Rejected,
/// writes <b>no</b> price observation (D5), and optionally records a negative <see cref="DealMatchMemory"/>
/// (DL-O3) so the pattern is not re-surfaced next pull.
/// </summary>
public sealed class RejectDealTests
{
    private readonly HouseholdId _household = HouseholdId.New();
    private readonly Guid _store = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    private static ValidityWindow Window() =>
        ValidityWindow.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7)).Value;

    private Deal StageDeal()
    {
        var raw = new RawDeal("Whole Milk 2L", "Dairyland", "2L", 3.99m, 2m, Guid.NewGuid(), "Save $1", Window());
        return Deal.Stage(
            _household, FlyerImportId.New(), _store, raw,
            DealNormalizer.Normalize("Whole Milk 2L"),
            MatchProposal.Unmatched(),
            new TestClock());
    }

    private RejectDeal Service(FakeDealRepository deals, FakeDealMatchMemoryRepository memories, TestClock clock) =>
        new(deals, memories, clock, new FakeTenantContext(_household.Value), NullLogger<RejectDeal>.Instance);

    [Fact(DisplayName = "Reject flips to Rejected and writes no observation (D5)")]
    public async Task Reject_WritesNoObservation()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var deals = new FakeDealRepository();
        deals.Items.Add(deal);
        var memories = new FakeDealMatchMemoryRepository();

        var result = await Service(deals, memories, clock).RejectAsync(deal.Id, _user);

        Assert.True(result.IsSuccess);
        Assert.Equal(DealStatus.Rejected, deal.Status);
        Assert.Null(deal.ProductId);
        Assert.Empty(memories.Items); // no negative memory unless asked
        Assert.Contains(deal.DomainEvents, e => e is DealRejectedEvent);
    }

    [Fact(DisplayName = "Reject with rememberNegative records a negative memory (DL-O3)")]
    public async Task Reject_RemembersNegative()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var deals = new FakeDealRepository();
        deals.Items.Add(deal);
        var memories = new FakeDealMatchMemoryRepository();

        var result = await Service(deals, memories, clock).RejectAsync(deal.Id, _user, rememberNegative: true);

        Assert.True(result.IsSuccess);
        var memory = Assert.Single(memories.Items);
        Assert.Equal("whole milk", memory.NormalizedName);
        Assert.Null(memory.ProductId); // negative memory = "not a tracked product"
        Assert.Equal(_store, memory.StoreId);
    }

    [Fact(DisplayName = "Reject with rememberNegative turns an existing positive memory negative")]
    public async Task Reject_ForgetsExistingPositiveMemory()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var deals = new FakeDealRepository();
        deals.Items.Add(deal);
        var memories = new FakeDealMatchMemoryRepository();
        memories.Items.Add(DealMatchMemory.Remember(
            _household, _store, DealNormalizer.Normalize("Whole Milk 2L"), "Whole Milk 2L", Guid.NewGuid(), _user, clock));

        var result = await Service(deals, memories, clock).RejectAsync(deal.Id, _user, rememberNegative: true);

        Assert.True(result.IsSuccess);
        var memory = Assert.Single(memories.Items); // repointed in place, not duplicated
        Assert.Null(memory.ProductId);
    }

    [Fact(DisplayName = "Reject is idempotent — a second reject does not re-emit the event")]
    public async Task Reject_IsIdempotent()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var deals = new FakeDealRepository();
        deals.Items.Add(deal);
        var memories = new FakeDealMatchMemoryRepository();
        var service = Service(deals, memories, clock);

        await service.RejectAsync(deal.Id, _user);
        deal.ClearDomainEvents();
        var second = await service.RejectAsync(deal.Id, _user);

        Assert.True(second.IsSuccess);
        Assert.Equal(DealStatus.Rejected, deal.Status);
        Assert.DoesNotContain(deal.DomainEvents, e => e is DealRejectedEvent);
    }

    [Fact(DisplayName = "Reject fails Unauthorized when there is no household in context")]
    public async Task Reject_NoHousehold_Unauthorized()
    {
        var clock = new TestClock();
        var deal = StageDeal();
        var deals = new FakeDealRepository();
        deals.Items.Add(deal);

        var service = new RejectDeal(
            deals, new FakeDealMatchMemoryRepository(), clock,
            new FakeTenantContext(null), NullLogger<RejectDeal>.Instance);
        var result = await service.RejectAsync(deal.Id, _user);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
    }

    [Fact(DisplayName = "Reject fails NotFound when the deal does not exist")]
    public async Task Reject_DealNotFound()
    {
        var clock = new TestClock();
        var deals = new FakeDealRepository();
        var result = await Service(deals, new FakeDealMatchMemoryRepository(), clock)
            .RejectAsync(DealId.New(), _user);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
    }
}
