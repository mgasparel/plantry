using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>A pinned IClock double so a WAF-hosted SUT and its fixture resolve the identical instant,
/// eliminating the midnight-tick race that occurs when both independently read the real system clock.</summary>
internal sealed class FixedClock(DateTimeOffset now, TimeZoneInfo? zone = null) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
    public TimeZoneInfo Zone { get; } = zone ?? TimeZoneInfo.Utc;
}

/// <summary>The single fixed instant every MealPlanning fragment-test WAF factory and fixture pins its
/// <see cref="FixedClock"/> to (plantry-1w87), so the SUT and the fixture that seeds it always agree on
/// "today" instead of racing two independent reads of the real system clock. A Tuesday — avoids
/// <c>MealPlan.NormalizeToMonday</c> week-boundary edge cases a Sunday/Monday date could introduce — at
/// noon UTC, far from any midnight boundary.</summary>
internal static class MealPlanningTestClock
{
    public static readonly DateTimeOffset Instant = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
}
