namespace Plantry.Planning.Domain;

/// <summary>
/// Transient DTO representing an AI-proposed meal for a single cell.
/// Not an EF entity — lives in-memory (pending store) only until accepted or discarded.
/// </summary>
public sealed record ProposedMeal(
    DateOnly Date,
    MealSlotId MealSlotId,
    IReadOnlyList<Guid> EffectiveAttendees,
    IReadOnlyList<ProposedDish> Dishes,
    string? Reasoning);

/// <summary>One proposed dish within a <see cref="ProposedMeal"/>.</summary>
public sealed record ProposedDish(
    Guid RecipeId,
    int Servings,
    int Ordinal,
    RecipeScoreBreakdown? ScoreBreakdown = null);

/// <summary>
/// Server-owned score evidence for one selected recipe. The three objective scores are normalized to
/// [0, 1], and their weighted contributions are the only values that make up <see cref="WeightedScore"/>.
/// Rating and preferred-tag signals are retained separately as deterministic tie-break evidence; they
/// never become an additive fourth objective.
/// </summary>
public sealed record RecipeScoreBreakdown(
    Guid RecipeId,
    decimal WeightedScore,
    decimal WasteScore,
    decimal CostScore,
    decimal VarietyScore,
    decimal WasteContribution,
    decimal CostContribution,
    decimal VarietyContribution,
    IReadOnlyList<RecipeFacetContribution> VarietyContributions,
    RecipeTieBreakSignals TieBreakSignals,
    CandidateCostCompleteness CostCompleteness = CandidateCostCompleteness.Unknown)
{
    /// <summary>Alias used by callers that describe the weighted objective as the objective score.</summary>
    public decimal ObjectiveScore => WeightedScore;
}

/// <summary>One marginal variety contribution for a confirmed or explicitly missing facet.</summary>
public sealed record RecipeFacetContribution(
    RecipeDiversityFacet Facet,
    decimal MarginalScore,
    decimal PriorUse,
    RecipeDiversityConfidence Confidence,
    IReadOnlyList<string> MatchedValues);

/// <summary>
/// Signals used only after normalized objective scores tie at the optimizer's documented precision.
/// Keeping them in a separate structure makes it impossible to mistake them for the visible 100%
/// Waste/Cost/Variety allocation.
/// </summary>
public sealed record RecipeTieBreakSignals(
    decimal PreferredTagSignal,
    decimal RatingSignal,
    int CostEvidenceRank,
    int WasteEvidenceRank);
