namespace Plantry.SharedKernel.Domain;

/// <summary>The shared D4 definition of a frequently purchased staple.</summary>
public static class FrequentStaplePredicate
{
    public const int LookbackDays = 90;
    public const int MinimumDistinctPurchaseDates = 3;

    public static bool IsFrequent(IEnumerable<DateOnly?> purchaseDates, DateOnly today)
    {
        var cutoff = today.AddDays(-LookbackDays);
        return purchaseDates
            .Where(date => date is { } value && value >= cutoff)
            .Select(date => date!.Value)
            .Distinct()
            .Count() >= MinimumDistinctPurchaseDates;
    }
}
