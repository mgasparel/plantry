using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Aggregate root — the household's canonical definition of a dish (recipes-domain-model.md §1).
/// This P2-0 step maps only the persistable shape so the <c>recipe</c> table and its child FKs exist;
/// authoring behaviour (ingredient/tag/photo management, soft-delete via <see cref="ArchivedAt"/>, the
/// <c>ReplaceIngredients</c> contiguity/≥1 invariants) and the aggregate's EF child-collection mapping
/// land in P2-1. <see cref="Directions"/> is a single text field — steps/sections are derived at render
/// (Resolved call 4), never persisted as rows.
/// </summary>
public sealed class Recipe : AggregateRoot<RecipeId>
{
    public HouseholdId HouseholdId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Source { get; private set; }
    public int? CookTimeMinutes { get; private set; }
    public int DefaultServings { get; private set; }
    public string? Directions { get; private set; }

    /// <summary>Soft-delete marker (Resolved call 1); a recipe with cook history is never physically removed.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Recipe() { } // EF
}
