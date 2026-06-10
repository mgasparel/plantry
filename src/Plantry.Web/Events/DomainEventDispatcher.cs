using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Events;

/// <summary>
/// Composition-root <see cref="IDomainEventDispatcher"/>: for each event, resolves every registered
/// <see cref="IDomainEventHandler{TEvent}"/> for that event's concrete type from the request scope and
/// invokes them in turn. Reflection over the closed handler type keeps the dispatcher itself
/// event-agnostic — new events only need a handler registration, no change here.
/// </summary>
public sealed class DomainEventDispatcher(IServiceProvider services) : IDomainEventDispatcher
{
    public async Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken ct = default)
    {
        foreach (var domainEvent in events)
        {
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
            var handleMethod = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;

            foreach (var handler in services.GetServices(handlerType))
                await (Task)handleMethod.Invoke(handler, [domainEvent, ct])!;
        }
    }
}
