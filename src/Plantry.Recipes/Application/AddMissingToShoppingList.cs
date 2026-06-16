using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service for the J5 "Add missing to shopping list" flow
/// (recipes-domain-model.md §7, recipes-journeys.md J5).
///
/// <para>Given a <see cref="RecipeId"/> and a <paramref name="desiredServings"/> count,
/// this service:
/// <list type="number">
///   <item>Loads the recipe aggregate.</item>
///   <item>Computes a FRESH <see cref="FulfillmentResult"/> at the desired serving count via
///     <see cref="FulfillmentService"/> (never reuses a cached result).</item>
///   <item>Takes only the <see cref="IngredientStatus.Missing"/> lines.</item>
///   <item>Excludes untracked staples (status <see cref="IngredientStatus.Untracked"/>  —
///     they are always satisfied by definition, C12 / recipes-journeys.md J5 edge case).</item>
///   <item>Scales each ingredient's required quantity by
///     <c>desiredServings / recipe.DefaultServings</c>.</item>
///   <item>Calls <see cref="IShoppingListWriter.AddItemsAsync"/> with those lines,
///     <c>source = "recipe"</c>, and <c>sourceRef = recipeId</c>.</item>
/// </list>
/// </para>
/// <para>Merge/no-dup logic is Shopping's concern (DM-18, shopping.md resolved call 5) — this
/// service passes all missing lines without pre-filtering for existing shopping-list entries.
/// </para>
/// </summary>
public sealed class AddMissingToShoppingList(
    IRecipeRepository recipes,
    FulfillmentService fulfillmentService,
    IShoppingListWriter shoppingWriter,
    IClock clock,
    ITenantContext tenant)
{
    /// <summary>Provenance string stamped on every row written via this service (DM-18).</summary>
    public const string RecipeSource = "recipe";

    public async Task<AddMissingResult> ExecuteAsync(
        RecipeId recipeId,
        int desiredServings,
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return new AddMissingResult.Unauthorized();

        if (desiredServings < 1)
            return new AddMissingResult.Invalid(
                Error.Custom("Recipes.InvalidServings", "Desired servings must be at least 1."));

        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return new AddMissingResult.NotFound();

        // Compute fulfillment FRESH at the desired serving count (recipes-domain-model.md §7:
        // "from a fresh FulfillmentResult at the displayed servings").
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var fulfillment = await fulfillmentService.ComputeAsync(recipe, desiredServings, today, ct);

        var scale = (decimal)desiredServings / recipe.DefaultServings;

        // Index ingredient lines by IngredientId so we can look up Quantity and UnitId.
        var ingredientIndex = recipe.Ingredients
            .ToDictionary(i => i.Id);

        var itemsToAdd = new List<ShoppingItem>();

        foreach (var line in fulfillment.Lines)
        {
            // Only Missing lines (excludes InStock, Low, and Untracked — C12 / J5 edge case).
            if (line.Status != IngredientStatus.Missing)
                continue;

            if (!ingredientIndex.TryGetValue(line.IngredientId, out var ingredient))
                continue; // defensive: should not happen if FulfillmentService and Recipe are consistent

            // Untracked staples have null Quantity/UnitId (R5) — skip (C12).
            // FulfillmentService classifies them as Untracked, not Missing, but guard here for safety.
            if (ingredient.Quantity is null || ingredient.UnitId is null)
                continue;

            var scaledQuantity = ingredient.Quantity.Value * scale;

            itemsToAdd.Add(new ShoppingItem(
                ProductId: ingredient.ProductId,
                Quantity: scaledQuantity,
                UnitId: ingredient.UnitId.Value));
        }

        if (itemsToAdd.Count == 0)
            return new AddMissingResult.NothingMissing();

        await shoppingWriter.AddItemsAsync(itemsToAdd, RecipeSource, recipeId.Value, ct);

        return new AddMissingResult.Added(itemsToAdd.Count);
    }
}

// ── Result discriminated union ──────────────────────────────────────────────────────────────────

/// <summary>The outcome of an <see cref="AddMissingToShoppingList.ExecuteAsync"/> call.</summary>
public abstract record AddMissingResult
{
    private AddMissingResult() { }

    /// <summary>Items were successfully added to the shopping list.</summary>
    /// <param name="ItemCount">Number of missing-ingredient lines dispatched to Shopping.</param>
    public sealed record Added(int ItemCount) : AddMissingResult;

    /// <summary>All tracked ingredients are in stock or low; nothing was Missing.</summary>
    public sealed record NothingMissing : AddMissingResult;

    /// <summary>The recipe does not exist in this household.</summary>
    public sealed record NotFound : AddMissingResult;

    /// <summary>The caller is not authenticated / no household context.</summary>
    public sealed record Unauthorized : AddMissingResult;

    /// <summary>Invalid input (e.g. desiredServings &lt; 1).</summary>
    public sealed record Invalid(Error Error) : AddMissingResult;
}
