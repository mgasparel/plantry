using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

public sealed class RecipeRatingTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly RecipeId Recipe = RecipeId.New();
    private static readonly Guid User = Guid.NewGuid();

    [Fact(DisplayName = "Create sets HouseholdId/RecipeId/UserId/Stars and stamps CreatedAt/UpdatedAt")]
    public void Create_Sets_Fields()
    {
        // A fixed instant, not SystemClock.Instance: Create reads clock.UtcNow twice (CreatedAt then
        // UpdatedAt) and the real wall clock can tick between those two reads, making the "same instant"
        // assertion below flaky.
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rating = RecipeRating.Create(Household, Recipe, User, 4, new FixedClock(now));

        Assert.Equal(Household, rating.HouseholdId);
        Assert.Equal(Recipe, rating.RecipeId);
        Assert.Equal(User, rating.UserId);
        Assert.Equal(4, rating.Stars);
        Assert.Equal(now, rating.CreatedAt);
        Assert.Equal(now, rating.UpdatedAt);
    }

    [Theory(DisplayName = "Create rejects stars outside 1..5")]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(100)]
    public void Create_Rejects_Invalid_Stars(int stars)
    {
        Assert.Throws<ArgumentException>(() => RecipeRating.Create(Household, Recipe, User, stars, Clock));
    }

    [Theory(DisplayName = "Create accepts every value in 1..5")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Create_Accepts_Valid_Stars(int stars)
    {
        var rating = RecipeRating.Create(Household, Recipe, User, stars, Clock);
        Assert.Equal(stars, rating.Stars);
    }

    [Fact(DisplayName = "SetStars updates Stars and bumps UpdatedAt")]
    public void SetStars_Updates_Stars_And_UpdatedAt()
    {
        var earlier = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = earlier.AddDays(1);
        var rating = RecipeRating.Create(Household, Recipe, User, 3, new FixedClock(earlier));

        rating.SetStars(5, new FixedClock(later));

        Assert.Equal(5, rating.Stars);
        Assert.Equal(later, rating.UpdatedAt);
        Assert.Equal(earlier, rating.CreatedAt);
    }

    [Theory(DisplayName = "SetStars rejects stars outside 1..5 and leaves the existing value untouched")]
    [InlineData(0)]
    [InlineData(6)]
    public void SetStars_Rejects_Invalid_Stars(int stars)
    {
        var rating = RecipeRating.Create(Household, Recipe, User, 3, Clock);

        Assert.Throws<ArgumentException>(() => rating.SetStars(stars, Clock));
        Assert.Equal(3, rating.Stars);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
        public TimeZoneInfo Zone { get; } = TimeZoneInfo.Utc;
    }
}
