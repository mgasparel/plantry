using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Plantry.Web.Intake;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// Unit coverage for <see cref="ReceiptUploadRateLimiter"/>: the burst window and the daily cap each reject
/// once their threshold is crossed, rejection carries a <c>RetryAfter</c> value, and the counters are
/// partitioned per household so one household's flood cannot lock out another. The windows are minutes/hours
/// long, so no test crosses a replenishment boundary — exhausting a window is deterministic within a run.
/// </summary>
public sealed class ReceiptUploadRateLimiterTests
{
    private static ReceiptUploadRateLimiter Build(int burst, int daily) =>
        new(Options.Create(new ReceiptUploadRateLimitOptions
        {
            BurstPermitLimit = burst,
            BurstWindowSeconds = 60,
            DailyPermitLimit = daily,
            DailyWindowHours = 24,
        }));

    [Fact]
    public void Admits_up_to_the_burst_limit_then_rejects()
    {
        using var limiter = Build(burst: 3, daily: 1000);

        for (var i = 0; i < 3; i++)
        {
            using var ok = limiter.AttemptAcquire("household-a");
            Assert.True(ok.IsAcquired, $"Upload #{i + 1} within the burst window should be admitted.");
        }

        using var rejected = limiter.AttemptAcquire("household-a");
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    public void Rejection_carries_a_retry_after_hint()
    {
        using var limiter = Build(burst: 1, daily: 1000);

        using (var first = limiter.AttemptAcquire("household-a"))
            Assert.True(first.IsAcquired);

        using var rejected = limiter.AttemptAcquire("household-a");
        Assert.False(rejected.IsAcquired);
        Assert.True(rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter),
            "A rejected lease must expose a Retry-After hint.");
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Enforces_the_daily_cap_independently_of_the_burst_window()
    {
        // Burst is generous so it never trips; the daily cap is what rejects the 3rd upload.
        using var limiter = Build(burst: 1000, daily: 2);

        using (var a = limiter.AttemptAcquire("household-a")) Assert.True(a.IsAcquired);
        using (var b = limiter.AttemptAcquire("household-a")) Assert.True(b.IsAcquired);

        using var overCap = limiter.AttemptAcquire("household-a");
        Assert.False(overCap.IsAcquired);
    }

    [Fact]
    public void Counters_are_partitioned_per_household()
    {
        using var limiter = Build(burst: 1, daily: 1000);

        using (var a1 = limiter.AttemptAcquire("household-a")) Assert.True(a1.IsAcquired);
        using (var a2 = limiter.AttemptAcquire("household-a")) Assert.False(a2.IsAcquired, "Household A is now over its burst limit.");

        // A different household has its own untouched window.
        using var b1 = limiter.AttemptAcquire("household-b");
        Assert.True(b1.IsAcquired, "Household B must not be throttled by household A's activity.");
    }
}
