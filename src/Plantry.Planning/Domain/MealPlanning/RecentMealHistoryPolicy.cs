namespace Plantry.Planning.Domain;

/// <summary>
/// The single product-policy location for retained meal history. History is a soft novelty signal,
/// never an eligibility rule: callers may score it but must not exclude a recipe because it appears here.
///
/// Retain ages 0 through 20 days. Weight is piecewise-linear through the approved anchors:
/// age 0 = 100%, age 7 = 20%, age 14 = 10%, age 21+ = 0%.
/// </summary>
public static class RecentMealHistoryPolicy
{
    public const int HorizonDays = 21;

    public static DateOnly EarliestRetainedDate(DateOnly asOfDate) =>
        asOfDate.AddDays(-(HorizonDays - 1));

    public static bool IsRetained(DateOnly occurredOn, DateOnly asOfDate) =>
        WeightFor(occurredOn, asOfDate) > 0m;

    public static decimal WeightFor(DateOnly occurredOn, DateOnly asOfDate)
    {
        var ageDays = asOfDate.DayNumber - occurredOn.DayNumber;
        if (ageDays < 0 || ageDays >= HorizonDays) return 0m;

        return ageDays switch
        {
            <= 7 => Interpolate(ageDays, 0, 1.00m, 7, 0.20m),
            <= 14 => Interpolate(ageDays, 7, 0.20m, 14, 0.10m),
            _ => Interpolate(ageDays, 14, 0.10m, 21, 0.00m),
        };
    }

    private static decimal Interpolate(int ageDays, int startAge, decimal startWeight, int endAge, decimal endWeight) =>
        startWeight + ((ageDays - startAge) * (endWeight - startWeight) / (endAge - startAge));
}
