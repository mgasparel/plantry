namespace Plantry.SharedKernel;

/// <summary>
/// A persistence-layer optimistic-concurrency conflict, surfaced to the application layer without
/// leaking the EF Core / provider exception type. A repository catches its provider's concurrency
/// failure (e.g. EF's <c>DbUpdateConcurrencyException</c> when an <c>xmin</c>/rowversion-guarded
/// UPDATE matches zero rows because another transaction won the race) and rethrows this instead, so
/// EF-free application services (which reference only the SharedKernel) can react to "someone changed
/// this row first" — typically by translating it into a domain <c>Result</c> failure.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException) { }
}
