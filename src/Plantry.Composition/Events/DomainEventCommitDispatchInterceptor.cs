using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Events;

/// <summary>
/// The commit half of transaction-aware domain-event dispatch (plantry-jvzk). Events raised inside an
/// explicit transaction are drained + cleared and BUFFERED by <see cref="DomainEventDispatchInterceptor"/>
/// on each inner SaveChanges; this interceptor flushes that buffer once the transaction actually
/// COMMITS and discards it if the transaction rolls back — so a rolled-back multi-save transaction (e.g.
/// an aborted Deals import materialization) dispatches <b>no</b> events, closing the pre-commit phantom
/// window. Registered on the same DbContexts as <see cref="DomainEventDispatchInterceptor"/>; the two share
/// the scoped <see cref="TransactionalDomainEventBuffer"/>.
/// <para>
/// This is <b>not</b> a transactional outbox: the residual "process crashes after commit but before the
/// handler runs" window (at-most-once) is unchanged and out of scope (ADR-014). It only moves dispatch from
/// an uncommitted inner save to the actual commit.
/// </para>
/// </summary>
public sealed class DomainEventCommitDispatchInterceptor(
    IDomainEventDispatcher dispatcher, TransactionalDomainEventBuffer buffer) : DbTransactionInterceptor
{
    public override async Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    {
        var events = buffer.Take(eventData.TransactionId);
        if (events.Count > 0)
            await dispatcher.DispatchAsync(events, ct);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        var events = buffer.Take(eventData.TransactionId);
        if (events.Count > 0)
            dispatcher.DispatchAsync(events).GetAwaiter().GetResult();
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    {
        buffer.Discard(eventData.TransactionId);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        buffer.Discard(eventData.TransactionId);
}
