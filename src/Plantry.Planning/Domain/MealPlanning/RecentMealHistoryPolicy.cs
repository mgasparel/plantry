namespace Plantry.Planning.Domain;

/// <summary>
/// The single product-policy location for retained meal history. History is a soft novelty signal,
/// never an eligibility rule: callers may score it but must not exclude a recipe because it appears here.
///
/// Retain ages 0 through 20 days. Curve D holds ages 0 through 3 at 100%, then
/// decays exponentially to zero at age 21: exp(-ln(100) * ((age - 3) / 18)^2).
/// Future local dates and age 21+ have zero weight.
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

        if (ageDays <= 3) return 1.00m;

        var normalizedAge = (ageDays - 3d) / (HorizonDays - 3d);
        var weight = Math.Exp(-Math.Log(100d) * normalizedAge * normalizedAge);
        return decimal.Round((decimal)weight, 12, MidpointRounding.ToEven);
    }
}
