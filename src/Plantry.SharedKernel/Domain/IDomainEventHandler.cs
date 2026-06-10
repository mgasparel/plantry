namespace Plantry.SharedKernel.Domain;

/// <summary>
/// Handles a domain event of type <typeparamref name="TEvent"/> after the originating write commits.
/// Multiple handlers may be registered per event; the dispatcher invokes each. Handlers run in the
/// same request scope as the write, but outside its transaction (post-save).
/// </summary>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct = default);
}
