using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Events;

/// <summary>
/// After a successful SaveChanges, drains the buffered <see cref="IDomainEvent"/>s from every tracked
/// aggregate and clears them. Events are cleared before dispatch/buffering so a handler that itself triggers
/// a further SaveChanges cannot re-dispatch the same batch. Registered per-DbContext via the DI options.
/// <para>
/// <b>Transaction-aware dispatch (plantry-jvzk).</b> The contract is "handlers only fire once the write has
/// actually committed." For a bare SaveChanges that holds universally — EF wraps it in an implicit
/// transaction that commits before this callback runs — so we dispatch immediately. But inside an
/// <b>explicit</b> multi-save transaction the write is <i>not</i> yet committed when an inner SaveChanges
/// fires, so dispatching here would emit a phantom event for a write that a later rollback discards. When
/// <see cref="DatabaseFacade.CurrentTransaction"/> is non-null we therefore BUFFER the drained events into
/// <see cref="TransactionalDomainEventBuffer"/> keyed by the transaction id;
/// <see cref="DomainEventCommitDispatchInterceptor"/> flushes them on commit and discards them on rollback.
/// </para>
/// </summary>
public sealed class DomainEventDispatchInterceptor(
    IDomainEventDispatcher dispatcher, TransactionalDomainEventBuffer buffer) : SaveChangesInterceptor
{
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        if (eventData.Context is { } context)
        {
            var aggregates = context.ChangeTracker.Entries<IHasDomainEvents>()
                .Where(e => e.Entity.DomainEvents.Count > 0)
                .Select(e => e.Entity)
                .ToList();

            var events = aggregates.SelectMany(a => a.DomainEvents).ToList();
            foreach (var aggregate in aggregates)
                aggregate.ClearDomainEvents();

            var transaction = context.Database.CurrentTransaction;
            if (transaction is null)
                // Implicit transaction (single save): EF has already committed before this fires, so the
                // write is durable — dispatch now, preserving the historical post-commit behaviour.
                await dispatcher.DispatchAsync(events, ct);
            else
                // Inside an explicit transaction: the write is not yet committed. Hold the events until the
                // transaction commits so a rollback dispatches nothing (no phantom events).
                buffer.Buffer(transaction.TransactionId, events);
        }

        return await base.SavedChangesAsync(eventData, result, ct);
    }
}
