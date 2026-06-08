using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Domain;

public sealed class CategoryTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    [Fact]
    public void Create_Sets_Properties_And_Trims_Name()
    {
        var category = Category.Create(HouseholdId, "  Dairy  ", defaultDueDays: 7, sortOrder: 3);

        Assert.Equal("Dairy", category.Name);
        Assert.Equal(7, category.DefaultDueDays);
        Assert.Equal(3, category.SortOrder);
    }

    [Fact]
    public void Create_Allows_Null_DefaultDueDays()
    {
        var category = Category.Create(HouseholdId, "Dairy");

        Assert.Null(category.DefaultDueDays);
        Assert.Equal(0, category.SortOrder);
    }

    [Fact]
    public void Create_Rejects_Negative_DefaultDueDays()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Category.Create(HouseholdId, "Dairy", defaultDueDays: -1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Rejects_Blank_Name(string name)
    {
        Assert.Throws<ArgumentException>(() => Category.Create(HouseholdId, name));
    }

    [Fact]
    public void Rename_Trims_And_Updates_Name()
    {
        var category = Category.Create(HouseholdId, "Dairy");

        category.Rename("  Dairy & Eggs  ");

        Assert.Equal("Dairy & Eggs", category.Name);
    }

    [Fact]
    public void SetDefaultDueDays_Rejects_Negative()
    {
        var category = Category.Create(HouseholdId, "Dairy");

        Assert.Throws<ArgumentOutOfRangeException>(() => category.SetDefaultDueDays(-1));
    }

    [Fact]
    public void SetDefaultDueDays_Allows_Clearing_To_Null()
    {
        var category = Category.Create(HouseholdId, "Dairy", defaultDueDays: 7);

        category.SetDefaultDueDays(null);

        Assert.Null(category.DefaultDueDays);
    }

    [Fact]
    public void SetSortOrder_Updates_Value()
    {
        var category = Category.Create(HouseholdId, "Dairy");

        category.SetSortOrder(42);

        Assert.Equal(42, category.SortOrder);
    }

    [Fact]
    public void New_Category_Is_Not_Archived()
    {
        var category = Category.Create(HouseholdId, "Dairy");

        Assert.False(category.IsArchived);
        Assert.Null(category.ArchivedAt);
    }

    [Fact]
    public void Archive_Sets_ArchivedAt_And_IsArchived()
    {
        var category = Category.Create(HouseholdId, "Dairy");

        category.Archive(Clock);

        Assert.True(category.IsArchived);
        Assert.NotNull(category.ArchivedAt);
    }

    [Fact]
    public void Archive_Is_Idempotent()
    {
        var category = Category.Create(HouseholdId, "Dairy");
        category.Archive(Clock);
        var firstArchivedAt = category.ArchivedAt;

        category.Archive(Clock);

        Assert.Equal(firstArchivedAt, category.ArchivedAt);
    }

    [Fact]
    public void Unarchive_Clears_ArchivedAt()
    {
        var category = Category.Create(HouseholdId, "Dairy");
        category.Archive(Clock);

        category.Unarchive();

        Assert.False(category.IsArchived);
        Assert.Null(category.ArchivedAt);
    }
}
