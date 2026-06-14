using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Ordered child of <see cref="Recipe"/> — one required item per row (recipes-domain-model.md §1, O1).
/// Wholesale-replaced with fresh ids on every save (Resolved call 2); nothing outside the aggregate
/// quotes an <see cref="IngredientId"/>. R5 (qty/unit both-or-neither) and ordinal-contiguity (R6)
/// are enforced by <see cref="Recipe.ReplaceIngredients"/>.
/// </summary>
public sealed class Ingredient : Entity<IngredientId>
{
    public HouseholdId HouseholdId { get; private set; }
    public RecipeId RecipeId { get; private set; }

    /// <summary>Soft ref → <c>catalog.product</c> (DM-3); never null (R4 / C12).</summary>
    public Guid ProductId { get; private set; }

    /// <summary>Null only for an untracked staple ("to taste"); paired with <see cref="UnitId"/> (R5).</summary>
    public decimal? Quantity { get; private set; }

    /// <summary>Soft ref → <c>catalog.unit</c> (DM-3); null only for an untracked staple (R5).</summary>
    public Guid? UnitId { get; private set; }

    public string? GroupHeading { get; private set; }
    public int Ordinal { get; private set; }

    private Ingredient() { } // EF

    internal static Ingredient Create(
        IngredientId id,
        HouseholdId householdId,
        RecipeId recipeId,
        Guid productId,
        decimal? quantity,
        Guid? unitId,
        string? groupHeading,
        int ordinal)
    {
        return new Ingredient
        {
            Id = id,
            HouseholdId = householdId,
            RecipeId = recipeId,
            ProductId = productId,
            Quantity = quantity,
            UnitId = unitId,
            GroupHeading = groupHeading,
            Ordinal = ordinal,
        };
    }
}
