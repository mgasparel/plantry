using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Integration.Infrastructure;

/// <summary>
/// A fixed <see cref="IClock"/> pinned to midnight UTC on a given date — shared across the Housekeeping
/// L3 tests (<c>StockDetectorsTests</c>, <c>RecipeDetectorsTests</c>,
/// <c>StockFactsReadModelRlsIsolationTests</c>) so none of them read the ambient wall clock, which would
/// make date-boundary assertions (e.g. D3's expiry grace window, D4's 90-day lookback) flaky depending on
/// when the suite runs.
/// </summary>
public sealed class FixedClock(DateOnly today) : IClock
{
    public DateTimeOffset UtcNow { get; } = new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
