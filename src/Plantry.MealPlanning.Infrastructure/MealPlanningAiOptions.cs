namespace Plantry.MealPlanning.Infrastructure;

/// <summary>
/// MealPlanning-owned AI seam flag, bound from the same <c>AI</c> configuration section as the generic
/// <c>Plantry.Ai.Infrastructure.AiOptions</c> (section reuse is a composition concern, not compile-time
/// coupling). This is a deterministic, no-network test seam for the meal planner — it belongs to the
/// MealPlanning context, not the shared AI library, so the shared library stays generic.
/// </summary>
public sealed class MealPlanningAiOptions
{
    public const string SectionName = "AI";

    /// <summary>
    /// Test-only seam: when true, the Web host registers a deterministic, no-network fake
    /// <c>IMealPlanner</c> instead of the real AI planner, so the meal-planning generate flow runs
    /// without any AI call or API key. Defaults to false — production always uses the real planner.
    /// Set only by the E2E AppHost; never enable it outside a test run.
    /// </summary>
    public bool UseFakePlanner { get; set; } = false;
}
