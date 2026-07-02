using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Domain;

public sealed class StoreTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    [Fact]
    public void Create_Sets_Properties_And_Trims_Name()
    {
        var store = Store.Create(HouseholdId, "  FreshCo  ", Clock);

        Assert.Equal("FreshCo", store.Name);
        Assert.Null(store.ExternalRef);
        Assert.False(store.IsArchived);
        Assert.Equal(store.CreatedAt, store.UpdatedAt);
    }

    [Fact]
    public void Create_With_ExternalRef_Trims_And_Stores_It()
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock, "  flipp-123  ");

        Assert.Equal("flipp-123", store.ExternalRef);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Treats_Blank_ExternalRef_As_Null(string externalRef)
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock, externalRef);

        Assert.Null(store.ExternalRef);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Rejects_Blank_Name(string name)
    {
        Assert.Throws<ArgumentException>(() => Store.Create(HouseholdId, name, Clock));
    }

    [Fact]
    public void Rename_Trims_And_Updates_Name()
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock);

        store.Rename("  Loblaws  ", Clock);

        Assert.Equal("Loblaws", store.Name);
    }

    [Fact]
    public void AdoptExternalRef_BackFills_And_Trims()
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock);

        store.AdoptExternalRef("  flipp-123  ", Clock);

        Assert.Equal("flipp-123", store.ExternalRef);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AdoptExternalRef_Rejects_Blank(string externalRef)
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock);

        Assert.Throws<ArgumentException>(() => store.AdoptExternalRef(externalRef, Clock));
    }

    [Fact]
    public void New_Store_Is_Not_Archived()
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock);

        Assert.False(store.IsArchived);
        Assert.Null(store.ArchivedAt);
    }

    [Fact]
    public void Archive_Sets_ArchivedAt_And_IsArchived()
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock);

        store.Archive(Clock);

        Assert.True(store.IsArchived);
        Assert.NotNull(store.ArchivedAt);
    }

    [Fact]
    public void Archive_Is_Idempotent()
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock);
        store.Archive(Clock);
        var firstArchivedAt = store.ArchivedAt;

        store.Archive(Clock);

        Assert.Equal(firstArchivedAt, store.ArchivedAt);
    }

    [Fact]
    public void Unarchive_Clears_ArchivedAt()
    {
        var store = Store.Create(HouseholdId, "FreshCo", Clock);
        store.Archive(Clock);

        store.Unarchive(Clock);

        Assert.False(store.IsArchived);
        Assert.Null(store.ArchivedAt);
    }
}
