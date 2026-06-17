namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Value object representing the relative importance weights for the three AI planning objectives:
/// Waste reduction, Cost minimisation, and Variety. Values must sum to 100 (M11, C14).
/// </summary>
public sealed record PlanningWeights
{
    /// <summary>Default weights lean toward Waste reduction (C14).</summary>
    public static PlanningWeights Default { get; } = new(60, 20, 20);

    public int Waste { get; }
    public int Cost { get; }
    public int Variety { get; }

    public PlanningWeights(int waste, int cost, int variety)
    {
        if (waste + cost + variety != 100)
            throw new ArgumentException(
                $"PlanningWeights must sum to 100 (got {waste + cost + variety}).");
        if (waste < 0 || cost < 0 || variety < 0)
            throw new ArgumentException("PlanningWeights values must be non-negative.");

        Waste = waste;
        Cost = cost;
        Variety = variety;
    }
}
