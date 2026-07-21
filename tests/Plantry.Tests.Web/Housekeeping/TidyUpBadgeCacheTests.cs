using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Housekeeping;
using Xunit;

namespace Plantry.Tests.Web.Housekeeping;

/// <summary>
/// L2 unit tests for <see cref="TidyUpBadgeCache"/> — the T6 in-memory badge cache: TTL expiry,
/// dismiss/restore invalidation, and per-household isolation.
/// </summary>
public sealed class TidyUpBadgeCacheTests
{
    private static readonly HouseholdId HouseholdA = HouseholdId.New();
    private static readonly HouseholdId HouseholdB = HouseholdId.New();

    [Fact(DisplayName = "TryGet before any Set — returns null (cache miss)")]
    public async Task TryGet_BeforeSet_ReturnsNull()
    {
        var cache = new TidyUpBadgeCache(new FixedClock(DateTimeOffset.UtcNow));

        Assert.Null(await cache.TryGetAsync(HouseholdA));
    }

    [Fact(DisplayName = "Set then TryGet — returns the stored count")]
    public async Task Set_ThenTryGet_ReturnsStoredCount()
    {
        var cache = new TidyUpBadgeCache(new FixedClock(DateTimeOffset.UtcNow));

        await cache.SetAsync(HouseholdA, 4);

        Assert.Equal(4, await cache.TryGetAsync(HouseholdA));
    }

    [Fact(DisplayName = "Invalidate — clears the cached count for that household")]
    public async Task Invalidate_ClearsStoredCount()
    {
        var cache = new TidyUpBadgeCache(new FixedClock(DateTimeOffset.UtcNow));
        await cache.SetAsync(HouseholdA, 4);

        await cache.InvalidateAsync(HouseholdA);

        Assert.Null(await cache.TryGetAsync(HouseholdA));
    }

    [Fact(DisplayName = "Per-household isolation — setting one household's count never leaks to another")]
    public async Task PerHousehold_Isolated()
    {
        var cache = new TidyUpBadgeCache(new FixedClock(DateTimeOffset.UtcNow));

        await cache.SetAsync(HouseholdA, 4);
        await cache.SetAsync(HouseholdB, 7);

        Assert.Equal(4, await cache.TryGetAsync(HouseholdA));
        Assert.Equal(7, await cache.TryGetAsync(HouseholdB));
    }

    [Fact(DisplayName = "TTL expiry — a count older than ~10 minutes reads as a cache miss")]
    public async Task Expiry_AfterTtl_ReadsAsMiss()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var cache = new TidyUpBadgeCache(clock);
        await cache.SetAsync(HouseholdA, 4);

        clock.Set(clock.UtcNow.AddMinutes(11));

        Assert.Null(await cache.TryGetAsync(HouseholdA));
    }

    [Fact(DisplayName = "Within TTL — the count is still returned")]
    public async Task WithinTtl_StillReturned()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var cache = new TidyUpBadgeCache(clock);
        await cache.SetAsync(HouseholdA, 4);

        clock.Set(clock.UtcNow.AddMinutes(9));

        Assert.Equal(4, await cache.TryGetAsync(HouseholdA));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Set(DateTimeOffset value) => UtcNow = value;
    }
}
