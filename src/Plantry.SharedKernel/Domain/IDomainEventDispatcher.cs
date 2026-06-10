namespace Plantry.SharedKernel.Domain;

/// <summary>
/// Resolves and invokes the registered <see cref="IDomainEventHandler{TEvent}"/>s for a batch of
/// domain events. The composition root supplies the implementation; domain and application layers
/// depend only on this seam.
/// </summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken ct = default);
}
