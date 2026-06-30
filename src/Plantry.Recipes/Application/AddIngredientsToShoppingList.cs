using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service for the "Add all ingredients to shopping list" action (plantry-s1z).
///
/// <para>Given a <see cref="RecipeId"/> and a <paramref name="servings"/> count, this service:
/// <list type="number">
///   <item>Loads the recipe aggregate.</item>
///   <item>Collects every tracked ingredient that has a Quantity+UnitId (quantity-bearing).
///     Untracked staples (null Quantity/UnitId) are skipped — they cannot be expressed as
///     a shopping-list quantity.</item>
///   <item>Scales each ingredient's quantity to <paramref name="servings"/> (same scaling
///     formula used by <see cref="AddMissingToShoppingList"/>).</item>
///   <item>Calls <see cref="IShoppingListWriter.AddItemsAsync"/> with <c>source = "recipe"</c>
///     and <c>sourceRef = recipeId</c>.</item>
/// </list>
/// </para>
///
/// <para>Unlike <see cref="AddMissingToShoppingList"/>, this path does NOT consult inventory
/// stock. It emits the full scaled quantity for every tracked ingredient — the caller wants
/// "put all of this recipe on the list", not "what am I still short of". Duplicate-handling
/// (per-source contribution upsert, plantry-9scq) is Shopping's responsibility (DM-18).</para>
/// </summary>
public sealed class AddIngredientsToShoppingList(
    IRecipeRepository recipes,
    IShoppingListWriter shoppingWriter,
    ITenantContext tenant)
{
    /// <summary>Provenance string stamped on every row written via this service (DM-18).</summary>
    public const string RecipeSource = "recipe";

    public async Task<AddIngredientsResult> ExecuteAsync(
        RecipeId recipeId,
        int servings,
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return new AddIngredientsResult.Unauthorized();

        if (servings < 1)
            return new AddIngredientsResult.Invalid(
                Error.Custom("Recipes.InvalidServings", "Desired servings must be at least 1."));

        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return new AddIngredientsResult.NotFound();

        // Scale factor: desiredServings / defaultServings.
        var scale = (decimal)servings / recipe.DefaultServings;

        // Collect all ingredients that have a Quantity+UnitId (skip untracked staples).
        var itemsToAdd = recipe.Ingredients
            .Where(i => i.Quantity.HasValue && i.UnitId.HasValue)
            .Select(i => new ShoppingItem(
                ProductId: i.ProductId,
                Quantity: Math.Round(i.Quantity!.Value * scale, 3),
                UnitId: i.UnitId!.Value))
            .ToList();

        if (itemsToAdd.Count == 0)
            return new AddIngredientsResult.NothingToAdd();

        await shoppingWriter.AddItemsAsync(itemsToAdd, RecipeSource, recipeId.Value, ct);

        return new AddIngredientsResult.Added(itemsToAdd.Count);
    }
}

// ── Result discriminated union ──────────────────────────────────────────────────────────────────

/// <summary>The outcome of an <see cref="AddIngredientsToShoppingList.ExecuteAsync"/> call.</summary>
public abstract record AddIngredientsResult
{
    private AddIngredientsResult() { }

    /// <summary>Items were successfully added to the shopping list.</summary>
    /// <param name="ItemCount">Number of ingredient lines dispatched to Shopping.</param>
    public sealed record Added(int ItemCount) : AddIngredientsResult;

    /// <summary>No tracked ingredient has a Quantity+UnitId (all are untracked staples).</summary>
    public sealed record NothingToAdd : AddIngredientsResult;

    /// <summary>The recipe does not exist in this household.</summary>
    public sealed record NotFound : AddIngredientsResult;

    /// <summary>The caller is not authenticated / no household context.</summary>
    public sealed record Unauthorized : AddIngredientsResult;

    /// <summary>Invalid input (e.g. servings &lt; 1).</summary>
    public sealed record Invalid(Error Error) : AddIngredientsResult;
}
