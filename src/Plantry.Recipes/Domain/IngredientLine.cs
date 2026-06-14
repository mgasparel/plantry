namespace Plantry.Recipes.Domain;

/// <summary>
/// Input DTO for <see cref="Recipe.ReplaceIngredients"/> — one ordered line from the author.
/// Ordinal is 0-based (the aggregate assigns contiguous ordinals from 0). R5: Quantity and UnitId
/// must both be set or both null. R4: ProductId must be non-empty.
/// </summary>
public sealed record IngredientLine(
    Guid ProductId,
    decimal? Quantity,
    Guid? UnitId,
    string? GroupHeading,
    int Ordinal);
