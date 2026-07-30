using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.SharedKernel.Domain;

/// <summary>
/// Unit tests for <see cref="ClockExtensions"/> (missing-seam:iclock, plantry-l639) — verified against a
/// fixed, non-local <see cref="TimeZoneInfo"/> so a regression to the machine's real
/// <c>TimeZoneInfo.Local</c> (or to <c>DateTimeOffset.LocalDateTime</c>, which reads it implicitly) fails
/// deterministically regardless of which zone the test happens to run in.
/// </summary>
public sealed class ClockExtensionsTests
{
    /// <summary>A fixed -05:00 zone, deliberately not the machine's real local zone.</summary>
    private static readonly TimeZoneInfo FixedWestZone =
        TimeZoneInfo.CreateCustomTimeZone("Fixed-05:00", TimeSpan.FromHours(-5), "Fixed -05:00", "Fixed -05:00");

    private sealed class FixedClock(DateTimeOffset now, TimeZoneInfo zone) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
        public TimeZoneInfo Zone { get; } = zone;
    }

    [Fact]
    public void LocalNow_Converts_UtcNow_Into_The_Clocks_Zone()
    {
        var utcNow = new DateTimeOffset(2026, 3, 10, 2, 30, 0, TimeSpan.Zero); // 02:30 UTC
        var clock = new FixedClock(utcNow, FixedWestZone);

        var localNow = clock.LocalNow();

        Assert.Equal(new DateTimeOffset(2026, 3, 9, 21, 30, 0, TimeSpan.FromHours(-5)), localNow);
    }

    [Fact]
    public void ToLocal_Converts_An_Arbitrary_Instant_Into_The_Clocks_Zone_Not_UtcNow()
    {
        // The clock's own UtcNow is unrelated to the instant being converted — ToLocal must convert
        // the PARAMETER, not fall back to reading UtcNow. A fixed sentinel (not DateTimeOffset.UtcNow)
        // keeps this test itself off the ambient wall clock, and is guaranteed distinct from `instant`.
        var clock = new FixedClock(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), FixedWestZone);
        var instant = new DateTimeOffset(2026, 6, 1, 4, 0, 0, TimeSpan.Zero); // 04:00 UTC

        var local = clock.ToLocal(instant);

        Assert.Equal(new DateTimeOffset(2026, 5, 31, 23, 0, 0, TimeSpan.FromHours(-5)), local);
    }

    [Fact]
    public void ToLocalDate_Returns_The_Local_Calendar_Day_Even_When_It_Differs_From_The_Utc_Calendar_Day()
    {
        // 02:30 UTC on the 10th is still 21:30 on the 9th at -05:00 — the local calendar day is one
        // day behind the UTC calendar day of the same instant.
        var instant = new DateTimeOffset(2026, 3, 10, 2, 30, 0, TimeSpan.Zero);
        var clock = new FixedClock(instant, FixedWestZone);

        var localDate = clock.ToLocalDate(instant);

        Assert.Equal(new DateOnly(2026, 3, 9), localDate);
        Assert.NotEqual(DateOnly.FromDateTime(instant.UtcDateTime), localDate);
    }

    [Fact]
    public void ToLocalDate_Agrees_With_The_Utc_Calendar_Day_For_A_Utc_Zoned_Clock()
    {
        var instant = new DateTimeOffset(2026, 3, 10, 2, 30, 0, TimeSpan.Zero);
        var clock = new FixedClock(instant, TimeZoneInfo.Utc);

        Assert.Equal(new DateOnly(2026, 3, 10), clock.ToLocalDate(instant));
    }

    private sealed class ZoneUnsetClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    [Fact]
    public void Zone_Defaults_To_Utc_For_A_Double_That_Does_Not_Override_It() =>
        Assert.Equal(TimeZoneInfo.Utc, ((IClock)new ZoneUnsetClock(new DateTimeOffset(2026, 3, 10, 2, 30, 0, TimeSpan.Zero))).Zone);

    [Fact]
    public void SystemClock_Zone_Is_The_Real_Machine_Local_Zone() =>
        Assert.Equal(TimeZoneInfo.Local, SystemClock.Instance.Zone);
}
