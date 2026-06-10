namespace Plantry.SharedKernel.Domain;

/// <summary>
/// Marks an aggregate that buffers <see cref="IDomainEvent"/>s for post-save dispatch. Implemented by
/// <see cref="AggregateRoot{TId}"/>; the SaveChanges interceptor uses it to find tracked aggregates
/// whose events should be drained and dispatched after a successful commit.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}
