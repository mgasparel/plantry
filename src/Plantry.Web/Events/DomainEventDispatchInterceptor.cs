using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Events;

/// <summary>
/// After a successful SaveChanges, drains the buffered <see cref="IDomainEvent"/>s from every tracked
/// aggregate, clears them, and dispatches them. Post-save (not pre-save) so handlers only fire once the
/// write has actually committed; events are cleared before dispatch so a handler that itself triggers a
/// further SaveChanges cannot re-dispatch the same batch. Registered per-DbContext via the DI options.
/// </summary>
public sealed class DomainEventDispatchInterceptor(IDomainEventDispatcher dispatcher) : SaveChangesInterceptor
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

            await dispatcher.DispatchAsync(events, ct);
        }

        return await base.SavedChangesAsync(eventData, result, ct);
    }
}
