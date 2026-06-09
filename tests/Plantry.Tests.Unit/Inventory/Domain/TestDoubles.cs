using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>A clock the tests can advance, so lot <c>created_at</c> values are controllable for FEFO.</summary>
internal sealed class MutableClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public MutableClock() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public MutableClock Advance(TimeSpan by)
    {
        UtcNow = UtcNow.Add(by);
        return this;
    }
}

/// <summary>Same-unit identity; any cross-unit pair passes through unchanged (single-unit scenarios).</summary>
internal sealed class IdentityQuantityConverter : IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
}

/// <summary>Always fails — stands in for an unresolvable cross-dimension conversion (fail-loud).</summary>
internal sealed class FailingQuantityConverter : IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) =>
        Error.Custom("Test.Unresolvable", "no conversion known");
}

/// <summary>Identity for same unit; otherwise multiplies by a configured per-pair factor, else fails.</summary>
internal sealed class FactorQuantityConverter(Dictionary<(Guid From, Guid To), decimal> factors) : IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId)
    {
        if (fromUnitId == toUnitId) return amount;
        if (factors.TryGetValue((fromUnitId, toUnitId), out var factor)) return amount * factor;
        return Error.Custom("Test.Unresolvable", $"no conversion from {fromUnitId} to {toUnitId}");
    }
}
