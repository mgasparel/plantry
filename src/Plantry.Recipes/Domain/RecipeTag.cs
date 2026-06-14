using Plantry.SharedKernel;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Membership join — a child of the <see cref="Recipe"/> aggregate (the tag set the recipe owns).
/// Composite PK <c>(recipe_id, tag_id)</c>, with composite FKs to both <c>recipe</c> (CASCADE) and
/// <c>tag</c> (RESTRICT). <c>SetTags</c> replaces the recipe's set wholesale.
/// </summary>
public sealed class RecipeTag
{
    public HouseholdId HouseholdId { get; private set; }
    public RecipeId RecipeId { get; private set; }
    public TagId TagId { get; private set; }

    private RecipeTag() { } // EF

    internal static RecipeTag Create(HouseholdId householdId, RecipeId recipeId, TagId tagId) =>
        new() { HouseholdId = householdId, RecipeId = recipeId, TagId = tagId };
}
