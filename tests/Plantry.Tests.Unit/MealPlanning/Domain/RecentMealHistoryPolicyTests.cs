using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

public sealed class RecentMealHistoryPolicyTests
{
    private static readonly DateOnly Today = new(2026, 8, 9);

    [Theory]
    [InlineData(0, 1.00)]
    [InlineData(7, 0.20)]
    [InlineData(14, 0.10)]
    [InlineData(21, 0.00)]
    [InlineData(30, 0.00)]
    public void WeightFor_UsesApprovedAnchorsAndHorizon(int ageDays, double expected)
    {
        var actual = RecentMealHistoryPolicy.WeightFor(Today.AddDays(-ageDays), Today);

        Assert.Equal((decimal)expected, actual);
    }

    [Fact]
    public void WeightFor_InterpolatesLinearlyBetweenAnchors()
    {
        var ageThree = RecentMealHistoryPolicy.WeightFor(Today.AddDays(-3), Today);
        var ageTen = RecentMealHistoryPolicy.WeightFor(Today.AddDays(-10), Today);
        var ageEighteen = RecentMealHistoryPolicy.WeightFor(Today.AddDays(-18), Today);

        Assert.Equal(0.657143m, Math.Round(ageThree, 6));
        Assert.Equal(0.157143m, Math.Round(ageTen, 6));
        Assert.Equal(0.042857m, Math.Round(ageEighteen, 6));
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
