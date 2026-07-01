using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Domain;

/// <summary>L1 unit tests for <see cref="StoreSubscription"/> (§3): subscribe/pause/resume/unsubscribe/record-pull.</summary>
public sealed class StoreSubscriptionTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Store = Guid.NewGuid();

    private static StoreSubscription NewSubscription(TestClock clock) =>
        StoreSubscription.Subscribe(Household, Store, "K1A0B1", clock);

    [Fact(DisplayName = "Subscribe starts active with the captured postal code")]
    public void Subscribe_StartsActive()
    {
        var clock = new TestClock();
        var sub = NewSubscription(clock);

        Assert.True(sub.IsActive);
        Assert.Equal("K1A0B1", sub.PostalCode);
        Assert.Equal(Store, sub.StoreId);
        Assert.Equal(clock.UtcNow, sub.CreatedAt);
        Assert.Null(sub.LastPulledAt);
    }

    [Fact(DisplayName = "Subscribe trims the postal code and rejects a blank one")]
    public void Subscribe_TrimsAndValidatesPostalCode()
    {
        var sub = StoreSubscription.Subscribe(Household, Store, "  K1A 0B1  ", new TestClock());
        Assert.Equal("K1A 0B1", sub.PostalCode);

        Assert.Throws<ArgumentException>(() =>
            StoreSubscription.Subscribe(Household, Store, "   ", new TestClock()));
    }

    [Fact(DisplayName = "Pause deactivates; Resume reactivates — history is retained")]
    public void PauseThenResume_TogglesActive()
    {
        var clock = new TestClock();
        var sub = NewSubscription(clock);

        sub.Pause(clock.Advance(TimeSpan.FromMinutes(1)));
        Assert.False(sub.IsActive);
        var pausedAt = sub.UpdatedAt;

        sub.Resume(clock.Advance(TimeSpan.FromMinutes(1)));
        Assert.True(sub.IsActive);
        Assert.True(sub.UpdatedAt > pausedAt);
    }

    [Fact(DisplayName = "Unsubscribe soft-deactivates without deleting")]
    public void Unsubscribe_SoftDeactivates()
    {
        var clock = new TestClock();
        var sub = NewSubscription(clock);

        sub.Unsubscribe(clock);

        Assert.False(sub.IsActive);
    }

    [Fact(DisplayName = "RecordPull stamps the last flyer external id and pull time (the dedup anchor)")]
    public void RecordPull_StampsBookkeeping()
    {
        var clock = new TestClock();
        var sub = NewSubscription(clock);

        clock.Advance(TimeSpan.FromHours(1));
        sub.RecordPull("flyer-abc-123", clock);

        Assert.Equal("flyer-abc-123", sub.LastFlyerExternalId);
        Assert.Equal(clock.UtcNow, sub.LastPulledAt);
    }

    [Fact(DisplayName = "RecordPull rejects a blank flyer external id")]
    public void RecordPull_RejectsBlankId()
    {
        var sub = NewSubscription(new TestClock());

        Assert.Throws<ArgumentException>(() => sub.RecordPull("  ", new TestClock()));
    }
}
