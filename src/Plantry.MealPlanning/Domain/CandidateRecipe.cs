namespace Plantry.MealPlanning.Domain;

/// <summary>
/// A recipe candidate supplied to the AI planner, containing the minimum facts needed for recipe selection.
/// Lives in Domain so it can be used by <see cref="ProposalAcl"/> without a circular dependency.
/// Cross-context read via <c>IRecipeReadModel</c> — MealPlanning never accesses Recipes tables directly.
/// </summary>
public sealed record CandidateRecipe(
    Guid RecipeId,
    string Name,
    IReadOnlyList<Guid> TagIds,
    int DefaultServings,
    decimal? CostPerServing);
