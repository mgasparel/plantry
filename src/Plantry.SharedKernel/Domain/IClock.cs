namespace Plantry.SharedKernel.Domain;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// The time zone <see cref="ClockExtensions"/> converts instants into for server-local wall-clock
    /// display/date logic (missing-seam:iclock, plantry-l639). Defaults to UTC so the many test doubles
    /// that never exercise zone-sensitive behaviour don't each need to implement this member — only
    /// <see cref="SystemClock"/> (real server-local time) and doubles that specifically test local-vs-UTC
    /// calendar-day behaviour need an explicit zone.
    /// </summary>
    TimeZoneInfo Zone => TimeZoneInfo.Utc;
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeZoneInfo Zone => TimeZoneInfo.Local;
}
