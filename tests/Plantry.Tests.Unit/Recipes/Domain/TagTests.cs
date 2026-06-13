using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

public sealed class TagTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    [Fact]
    public void Create_Sets_Properties_And_Trims_Name()
    {
        var tag = Tag.Create(HouseholdId, "  Vegetarian  ", TagCategory.Diet, Clock);

        Assert.Equal(HouseholdId, tag.HouseholdId);
        Assert.Equal("Vegetarian", tag.Name);
        Assert.Equal(TagCategory.Diet, tag.Category);
        Assert.Equal(tag.CreatedAt, tag.UpdatedAt);
    }

    [Fact]
    public void Create_Allows_Null_Category()
    {
        var tag = Tag.Create(HouseholdId, "Spicy", category: null, Clock);

        Assert.Null(tag.Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Rejects_Blank_Name(string name)
    {
        Assert.Throws<ArgumentException>(() => Tag.Create(HouseholdId, name, TagCategory.Diet, Clock));
    }

    [Fact]
    public void Rename_Trims_Updates_Name_And_Touches_UpdatedAt()
    {
        var tag = Tag.Create(HouseholdId, "Veg", TagCategory.Diet, new FixedClock(Origin));
        var clock = new FixedClock(Later);

        tag.Rename("  Vegetarian  ", clock);

        Assert.Equal("Vegetarian", tag.Name);
        Assert.Equal(Later, tag.UpdatedAt);
        Assert.Equal(Origin, tag.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_Rejects_Blank_Name(string name)
    {
        var tag = Tag.Create(HouseholdId, "Vegetarian", TagCategory.Diet, Clock);

        Assert.Throws<ArgumentException>(() => tag.Rename(name, Clock));
    }

    [Fact]
    public void SetCategory_Updates_Value_And_Touches_UpdatedAt()
    {
        var tag = Tag.Create(HouseholdId, "Beef", TagCategory.Diet, new FixedClock(Origin));
        var clock = new FixedClock(Later);

        tag.SetCategory(TagCategory.Protein, clock);

        Assert.Equal(TagCategory.Protein, tag.Category);
        Assert.Equal(Later, tag.UpdatedAt);
    }

    [Fact]
    public void SetCategory_Allows_Clearing_To_Null_And_Touches_UpdatedAt()
    {
        var tag = Tag.Create(HouseholdId, "Beef", TagCategory.Protein, new FixedClock(Origin));
        var clock = new FixedClock(Later);

        tag.SetCategory(null, clock);

        Assert.Null(tag.Category);
        Assert.Equal(Later, tag.UpdatedAt);
    }

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
