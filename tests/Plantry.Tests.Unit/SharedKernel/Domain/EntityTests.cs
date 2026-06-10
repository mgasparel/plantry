using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.SharedKernel.Domain;

public sealed class EntityTests
{
    private sealed class TestEntity : Entity<Guid>
    {
        public TestEntity(Guid id) : base(id) { }
    }

    private sealed class OtherEntity : Entity<Guid>
    {
        public OtherEntity(Guid id) : base(id) { }
    }

    [Fact]
    public void Equals_Same_Reference_Returns_True()
    {
        var entity = new TestEntity(Guid.NewGuid());

        Assert.True(entity.Equals(entity));
    }

    [Fact]
    public void Equals_Null_Returns_False()
    {
        var entity = new TestEntity(Guid.NewGuid());

        Assert.False(entity.Equals(null));
    }

    [Fact]
    public void Equals_Different_Concrete_Type_Returns_False()
    {
        var id = Guid.NewGuid();
        var entity = new TestEntity(id);
        var other = new OtherEntity(id);

        Assert.False(entity.Equals(other));
    }

    [Fact]
    public void Equals_Same_Type_Same_Id_Returns_True()
    {
        var id = Guid.NewGuid();
        var a = new TestEntity(id);
        var b = new TestEntity(id);

        Assert.True(a.Equals(b));
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_Same_Type_Different_Id_Returns_False()
    {
        var a = new TestEntity(Guid.NewGuid());
        var b = new TestEntity(Guid.NewGuid());

        Assert.False(a.Equals(b));
        Assert.True(a != b);
    }

    [Fact]
    public void GetHashCode_Is_Stable_Across_Calls()
    {
        var entity = new TestEntity(Guid.NewGuid());

        Assert.Equal(entity.GetHashCode(), entity.GetHashCode());
    }

    [Fact]
    public void EqualityOperator_Both_Null_Returns_True()
    {
        TestEntity? left = null;
        TestEntity? right = null;

        Assert.True(left == right);
    }

    [Fact]
    public void EqualityOperator_Left_Null_Returns_False()
    {
        TestEntity? left = null;
        var right = new TestEntity(Guid.NewGuid());

        Assert.False(left == right);
    }
}
