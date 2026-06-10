using Microsoft.Extensions.DependencyInjection;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Events;
using Xunit;

namespace Plantry.Tests.Integration.Intake;

/// <summary>
/// Focused tests for the composition-root <see cref="DomainEventDispatcher"/>: it resolves every
/// registered <see cref="IDomainEventHandler{TEvent}"/> for an event's concrete type and invokes each,
/// and is a no-op for an event with no registered handler. (No database — pure DI wiring.)
/// </summary>
public sealed class DomainEventDispatcherTests
{
    private static ImportSessionCommittedEvent SampleEvent() =>
        new(ImportSessionId.New(), HouseholdId.New(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Invokes_Every_Registered_Handler_For_The_Event_Type()
    {
        var first = new CapturingHandler();
        var second = new CapturingHandler();
        var dispatcher = new DomainEventDispatcher(new ServiceCollection()
            .AddSingleton<IDomainEventHandler<ImportSessionCommittedEvent>>(first)
            .AddSingleton<IDomainEventHandler<ImportSessionCommittedEvent>>(second)
            .BuildServiceProvider());

        var evt = SampleEvent();
        await dispatcher.DispatchAsync([evt]);

        Assert.Equal(evt, Assert.Single(first.Events));
        Assert.Equal(evt, Assert.Single(second.Events));
    }

    [Fact]
    public async Task Is_A_No_Op_When_No_Handler_Is_Registered()
    {
        var dispatcher = new DomainEventDispatcher(new ServiceCollection().BuildServiceProvider());

        // No handler registered for the event type — must not throw.
        await dispatcher.DispatchAsync([SampleEvent()]);
    }

    private sealed class CapturingHandler : IDomainEventHandler<ImportSessionCommittedEvent>
    {
        public List<ImportSessionCommittedEvent> Events { get; } = [];

        public Task HandleAsync(ImportSessionCommittedEvent domainEvent, CancellationToken ct = default)
        {
            Events.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
