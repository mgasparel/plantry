using Plantry.SharedKernel.Domain;

namespace Plantry.Web;

/// <summary>
/// Opt-in clock for the Testing AppHost. Production continues to use <see cref="SystemClock"/>;
/// the configuration seam lets a live E2E journey pin calendar-day projections and domain stamps
/// to one instant without changing the normal runtime clock.
/// </summary>
internal sealed class ConfiguredClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;

    public TimeZoneInfo Zone => TimeZoneInfo.Utc;
}
