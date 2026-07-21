using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Housekeeping;

/// <summary>A mutable clock for asserting timestamp stamping in Housekeeping aggregate/application behaviours.</summary>
internal sealed class TestClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public TestClock() : this(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)) { }

    public DateTimeOffset UtcNow => _now;

    public TestClock Advance(TimeSpan by)
    {
        _now = _now.Add(by);
        return this;
    }
}
