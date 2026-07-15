using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Ordered child of <see cref="Recipe"/> — a <b>sibling line type</b> next to <see cref="Ingredient"/>
/// (recipe-composition.md D1). Declares "<c>Servings</c> servings of <see cref="SubRecipeId"/>" as one
/// line in the parent's list. Amount is denominated in servings of the sub-recipe (D2), invariant under a
/// proportional rescale of the sub. Wholesale-replaced with fresh ids on every save (O1), sharing the
/// ordinal space with the ingredient lines (N3).
/// <para>
/// Invariants N1 (<c>Servings &gt; 0</c>) and N2 (<see cref="SubRecipeId"/> ≠ owning recipe id) are
/// enforced by <see cref="RecipeLineSet.Create"/>; the DAG / same-household / sub-existence checks (N4)
/// are cross-aggregate and live in the application layer.
/// </para>
/// </summary>
public sealed class Inclusion : Entity<InclusionId>
{
    public HouseholdId HouseholdId { get; private set; }

    /// <summary>The owning (parent) recipe.</summary>
    public RecipeId RecipeId { get; private set; }

    /// <summary>The included (sub) recipe — always the same household (checked in the application layer, N4).</summary>
    public RecipeId SubRecipeId { get; private set; }

    /// <summary>Servings of the sub-recipe (&gt; 0, N1).</summary>
    public decimal Servings { get; private set; }

    public string? GroupHeading { get; private set; }
    public int Ordinal { get; private set; }

    private Inclusion() { } // EF

    internal static Inclusion Create(
        InclusionId id,
        HouseholdId householdId,
        RecipeId recipeId,
        RecipeId subRecipeId,
        decimal servings,
        string? groupHeading,
        int ordinal)
    {
        return new Inclusion
        {
            Id = id,
            HouseholdId = householdId,
            RecipeId = recipeId,
            SubRecipeId = subRecipeId,
            Servings = servings,
            GroupHeading = groupHeading,
            Ordinal = ordinal,
        };
    }
}
