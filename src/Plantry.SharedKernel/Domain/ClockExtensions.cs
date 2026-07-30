namespace Plantry.SharedKernel.Domain;

/// <summary>
/// Server-local wall-clock helpers built on <see cref="IClock.Zone"/> (missing-seam:iclock, plantry-l639).
/// Production code must convert an instant to server-local time or a server-local calendar day through
/// these extensions — never via the <see cref="DateTimeOffset"/> members that silently convert through
/// the machine's own local zone (the <c>LocalDateTime</c> property and the <c>ToLocalTime</c> method),
/// bypassing the injected clock entirely, and never by reading <see cref="TimeZoneInfo"/>'s <c>Local</c>
/// zone directly at a call site.
/// </summary>
public static class ClockExtensions
{
    /// <summary>The current instant (<see cref="IClock.UtcNow"/>) converted into the clock's zone.</summary>
    public static DateTimeOffset LocalNow(this IClock clock) =>
        TimeZoneInfo.ConvertTime(clock.UtcNow, clock.Zone);

    /// <summary>Converts an arbitrary instant into the clock's zone.</summary>
    public static DateTimeOffset ToLocal(this IClock clock, DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, clock.Zone);

    /// <summary>The calendar day (in the clock's zone) that an arbitrary instant falls on.</summary>
    public static DateOnly ToLocalDate(this IClock clock, DateTimeOffset instant) =>
        DateOnly.FromDateTime(clock.ToLocal(instant).DateTime);
}
