using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.SharedKernel.Domain;

public sealed class AggregateRootTests
{
    private sealed class TestAggregate : AggregateRoot<Guid>
    {
        public TestAggregate(Guid id) : base(id) { }
        public void Raise(IDomainEvent e) => RaiseDomainEvent(e);
    }

    private sealed record TestEvent(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent;

    [Fact]
    public void New_Aggregate_Has_No_Domain_Events()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void RaiseDomainEvent_Appends_To_DomainEvents()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());
        var evt = new TestEvent(Guid.NewGuid(), DateTimeOffset.UtcNow);

        aggregate.Raise(evt);

        Assert.Single(aggregate.DomainEvents);
    }

    [Fact]
    public void ClearDomainEvents_Removes_All_Events()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());
        aggregate.Raise(new TestEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));

        aggregate.ClearDomainEvents();

        Assert.Empty(aggregate.DomainEvents);
    }
}
