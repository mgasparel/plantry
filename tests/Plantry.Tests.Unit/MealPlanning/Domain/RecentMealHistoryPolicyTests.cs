using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

public sealed class RecentMealHistoryPolicyTests
{
    private static readonly DateOnly Today = new(2026, 8, 9);

    [Theory]
    [InlineData(0, 1.00)]
    [InlineData(3, 1.00)]
    [InlineData(21, 0.00)]
    [InlineData(30, 0.00)]
    public void WeightFor_UsesCurveDAnchorsAndHorizon(int ageDays, double expected)
    {
        var actual = RecentMealHistoryPolicy.WeightFor(Today.AddDays(-ageDays), Today);

        Assert.Equal((decimal)expected, actual);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(20)]
    public void WeightFor_FollowsCurveDExponentialFormula(int ageDays)
    {
        var expected = Math.Exp(-Math.Log(100d) * Math.Pow((ageDays - 3d) / 18d, 2));

        Assert.Equal((decimal)expected, RecentMealHistoryPolicy.WeightFor(Today.AddDays(-ageDays), Today), 12);
    }

    [Fact]
    public void WeightFor_RejectsFutureOccurrences()
    {
        Assert.Equal(0m, RecentMealHistoryPolicy.WeightFor(Today.AddDays(1), Today));
    }

    [Fact]
    public void RecencyScore_SumsDistinctWeeklyOccurrences()
    {
        var history = new RecentRecipeHistory(
            Guid.NewGuid(),
            "Weekly meal",
            IsArchived: false,
            [
                new RecentMealOccurrence(Today.AddDays(-7), RecentMealOccurrenceSource.CookEvent, 0.20m),
                new RecentMealOccurrence(Today.AddDays(-14), RecentMealOccurrenceSource.RetainedPlan, 0.10m),
            ],
            []);

        Assert.Equal(0.30m, history.RecencyScore);
    }
}
