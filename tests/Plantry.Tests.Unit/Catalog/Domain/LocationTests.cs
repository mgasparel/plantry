using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Domain;

public sealed class LocationTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    [Fact]
    public void Create_Sets_Properties_And_Trims_Name()
    {
        var location = Location.Create(HouseholdId, "  Garage freezer  ", LocationType.Frozen);

        Assert.Equal("Garage freezer", location.Name);
        Assert.Equal(LocationType.Frozen, location.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Rejects_Blank_Name(string name)
    {
        Assert.Throws<ArgumentException>(() => Location.Create(HouseholdId, name, LocationType.Ambient));
    }

    [Fact]
    public void Rename_Trims_And_Updates_Name()
    {
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);

        location.Rename("  Kitchen pantry  ");

        Assert.Equal("Kitchen pantry", location.Name);
    }

    [Fact]
    public void Rename_Rejects_Blank_Name()
    {
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);

        Assert.Throws<ArgumentException>(() => location.Rename(" "));
    }

    [Theory]
    [InlineData(LocationType.Frozen, true)]
    [InlineData(LocationType.Ambient, false)]
    public void IsFrozen_Reflects_Type(LocationType type, bool expected)
    {
        var location = Location.Create(HouseholdId, "Somewhere", type);

        Assert.Equal(expected, location.IsFrozen);
    }

    [Fact]
    public void New_Location_Is_Not_Archived()
    {
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);

        Assert.False(location.IsArchived);
        Assert.Null(location.ArchivedAt);
    }

    [Fact]
    public void Archive_Sets_ArchivedAt_And_IsArchived()
    {
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);

        location.Archive(Clock);

        Assert.True(location.IsArchived);
        Assert.NotNull(location.ArchivedAt);
    }

    [Fact]
    public void Archive_Is_Idempotent()
    {
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);
        location.Archive(Clock);
        var firstArchivedAt = location.ArchivedAt;

        location.Archive(Clock);

        Assert.Equal(firstArchivedAt, location.ArchivedAt);
    }

    [Fact]
    public void Unarchive_Clears_ArchivedAt()
    {
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);
        location.Archive(Clock);

        location.Unarchive();

        Assert.False(location.IsArchived);
        Assert.Null(location.ArchivedAt);
    }

    [Fact]
    public void New_Location_Has_Never_Been_Counted()
    {
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);

        Assert.Null(location.LastCountedAt);
    }

    [Fact]
    public void MarkCounted_Sets_LastCountedAt()
    {
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);

        location.MarkCounted(Clock);

        Assert.NotNull(location.LastCountedAt);
    }

    [Fact]
    public void MarkCounted_Always_Advances_LastCountedAt_Unlike_Archive()
    {
        // Unlike Archive (idempotent — an "already done" guard), a second completed walk is a
        // fresh observation that must supersede the last one, so MarkCounted always overwrites.
        var fixedClock = new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var location = Location.Create(HouseholdId, "Pantry", LocationType.Ambient);
        location.MarkCounted(fixedClock);
        var firstCountedAt = location.LastCountedAt;

        var laterClock = new FixedClock(DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        location.MarkCounted(laterClock);

        Assert.NotEqual(firstCountedAt, location.LastCountedAt);
        Assert.Equal(laterClock.UtcNow, location.LastCountedAt);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
