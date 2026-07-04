using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Plantry.Web.Intake;

/// <summary>
/// Tunable limits for the receipt-upload abuse gate. Bound from the <c>Intake:UploadRateLimit</c>
/// configuration section; the defaults here (a short burst window plus a daily cap) apply when no
/// configuration is present, so the guard is always armed.
/// </summary>
public sealed class ReceiptUploadRateLimitOptions
{
    public const string SectionName = "Intake:UploadRateLimit";

    /// <summary>Uploads allowed within <see cref="BurstWindowSeconds"/> before a 429 (short-window burst guard).</summary>
    public int BurstPermitLimit { get; set; } = 10;

    /// <summary>Length of the burst window, in seconds.</summary>
    public int BurstWindowSeconds { get; set; } = 60;

    /// <summary>Uploads allowed within <see cref="DailyWindowHours"/> before a 429 (sustained-abuse cap).</summary>
    public int DailyPermitLimit { get; set; } = 100;

    /// <summary>Length of the daily window, in hours.</summary>
    public int DailyWindowHours { get; set; } = 24;
}

/// <summary>
/// Per-household rate limiter for receipt uploads (SPEC §2a abuse protection). Chains two fixed-window
/// limiters — a short burst window and a daily cap — over a partition key of the household id (falling
/// back to the user id when no household is armed). A request is admitted only when it clears <em>both</em>
/// windows; otherwise the lease reports <c>IsAcquired == false</c> and carries a <c>RetryAfter</c> metadata
/// value the caller surfaces as an HTTP <c>Retry-After</c> header alongside a 429.
///
/// <para>Registered as a singleton so the sliding counters persist across requests. Fixed-window leases do
/// not return permits on dispose (only the window timer replenishes), so callers may dispose the lease
/// immediately after inspecting it.</para>
/// </summary>
public sealed class ReceiptUploadRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public ReceiptUploadRateLimiter(IOptions<ReceiptUploadRateLimitOptions> options)
    {
        var o = options.Value;

        var burst = PartitionedRateLimiter.Create<string, string>(partitionKey =>
            RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = o.BurstPermitLimit,
                Window = TimeSpan.FromSeconds(o.BurstWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        var daily = PartitionedRateLimiter.Create<string, string>(partitionKey =>
            RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = o.DailyPermitLimit,
                Window = TimeSpan.FromHours(o.DailyWindowHours),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        _limiter = PartitionedRateLimiter.CreateChained(burst, daily);
    }

    /// <summary>
    /// Attempts to admit one upload for the given partition key. The returned lease is acquired only when
    /// both the burst and daily windows have headroom; on rejection it exposes the <c>RetryAfter</c> metadata.
    /// </summary>
    public RateLimitLease AttemptAcquire(string partitionKey) => _limiter.AttemptAcquire(partitionKey, 1);

    public void Dispose() => _limiter.Dispose();
}
