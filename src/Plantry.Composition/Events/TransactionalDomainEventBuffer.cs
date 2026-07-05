using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Events;

/// <summary>
/// Scoped hand-off between <see cref="DomainEventDispatchInterceptor"/> (which drains an aggregate's
/// <see cref="IDomainEvent"/>s on a save that runs <b>inside an explicit transaction</b>) and
/// <see cref="DomainEventCommitDispatchInterceptor"/> (which dispatches them only once that transaction
/// actually COMMITS). Holding the events here until commit is what makes the interceptor's stated contract
/// — "handlers only fire once the write has committed" — true for a multi-save explicit transaction, not
/// just for a bare single save: a rolled-back transaction's buffered events are discarded, never dispatched
/// (closing the pre-commit phantom-event window, plantry-jvzk).
/// <para>
/// Keyed by the EF <c>TransactionId</c> (a fresh <see cref="Guid"/> per transaction, never reused), so a
/// transaction abandoned via dispose-without-explicit-rollback simply never gets a matching
/// <see cref="Take"/> — its events are dropped, and they can never leak into a <i>later</i> transaction's
/// commit. The instance is scoped, so any residual entry is released when the DI scope ends. Not
/// thread-safe: EF interceptor callbacks for a scope's contexts run serially, and a DbContext is not
/// concurrency-safe regardless.
/// </para>
/// </summary>
public sealed class TransactionalDomainEventBuffer
{
    private readonly Dictionary<Guid, List<IDomainEvent>> _pending = [];

    /// <summary>Appends <paramref name="events"/> to the batch buffered for <paramref name="transactionId"/>.</summary>
    public void Buffer(Guid transactionId, IReadOnlyList<IDomainEvent> events)
    {
        if (events.Count == 0)
            return;

        if (_pending.TryGetValue(transactionId, out var batch))
            batch.AddRange(events);
        else
            _pending[transactionId] = [.. events];
    }

    /// <summary>Removes and returns the batch buffered for <paramref name="transactionId"/> (empty if none).</summary>
    public IReadOnlyList<IDomainEvent> Take(Guid transactionId) =>
        _pending.Remove(transactionId, out var batch) ? batch : [];

    /// <summary>Drops the batch buffered for <paramref name="transactionId"/> without dispatching (rollback).</summary>
    public void Discard(Guid transactionId) => _pending.Remove(transactionId);
}
