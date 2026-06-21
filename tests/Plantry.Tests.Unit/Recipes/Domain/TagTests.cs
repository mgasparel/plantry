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

    // ── Archive / Unarchive ──────────────────────────────────────────────────

    [Fact]
    public void Archive_Sets_ArchivedAt_And_IsArchived()
    {
        var tag = Tag.Create(HouseholdId, "Vegan", null, new FixedClock(Origin));
        Assert.False(tag.IsArchived);

        tag.Archive(new FixedClock(Later));

        Assert.True(tag.IsArchived);
        Assert.Equal(Later, tag.ArchivedAt);
        Assert.Equal(Later, tag.UpdatedAt);
    }

    [Fact]
    public void Archive_Is_Idempotent_And_Preserves_First_ArchivedAt()
    {
        var tag = Tag.Create(HouseholdId, "Vegan", null, new FixedClock(Origin));
        tag.Archive(new FixedClock(Later));

        var evenLater = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        tag.Archive(new FixedClock(evenLater));

        // ArchivedAt must not change on second call.
        Assert.Equal(Later, tag.ArchivedAt);
    }

    [Fact]
    public void Unarchive_Clears_ArchivedAt_And_IsArchived()
    {
        var tag = Tag.Create(HouseholdId, "Vegan", null, new FixedClock(Origin));
        tag.Archive(new FixedClock(Later));

        var evenLater = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        tag.Unarchive(new FixedClock(evenLater));

        Assert.False(tag.IsArchived);
        Assert.Null(tag.ArchivedAt);
        Assert.Equal(evenLater, tag.UpdatedAt);
    }

    [Fact]
    public void Unarchive_Is_Idempotent_When_Already_Active()
    {
        var tag = Tag.Create(HouseholdId, "Vegan", null, new FixedClock(Origin));
        // Not archived — calling Unarchive should be a no-op.
        tag.Unarchive(new FixedClock(Later));

        Assert.False(tag.IsArchived);
        // UpdatedAt should not change on no-op.
        Assert.Equal(Origin, tag.UpdatedAt);
    }

    [Fact]
    public void Create_IsArchived_Is_False()
    {
        var tag = Tag.Create(HouseholdId, "Fresh", null, Clock);
        Assert.False(tag.IsArchived);
        Assert.Null(tag.ArchivedAt);
    }

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
