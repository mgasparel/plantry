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
///   <item>Collects every ingredient that has a Quantity+UnitId (quantity-bearing) AND whose
///     product is stock-tracked in Catalog (<c>track_stock = true</c>). Ingredients with a null
///     Quantity/UnitId (untracked staples) and quantity-bearing ingredients whose product is
///     untracked or unknown to the household are skipped (C12, plantry-yukq).</item>
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
///
/// <para>"Tracked" here means <c>track_stock = true</c> in Catalog — the same definition the Cook
/// flow (C12, <see cref="CookRecipe"/>) and the <c>_DetailsFulfilmentCard</c> "tracked" label use.
/// A quantity-bearing ingredient whose product is untracked (a staple that carries an incidental
/// quantity) or is absent from this household's catalog is excluded, so the add-set matches the UI
/// label exactly and untracked staples never reach the shopping list (plantry-yukq).</para>
/// </summary>
public sealed class AddIngredientsToShoppingList(
    IRecipeRepository recipes,
    IShoppingListWriter shoppingWriter,
    ICatalogProductReader products,
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

        // Batch-resolve track_stock for every quantity-bearing candidate product in one catalog
        // round-trip, then keep only the products this household actually stock-tracks (C12, plantry-yukq).
        var candidateIds = recipe.Ingredients
            .Where(i => i.Quantity.HasValue && i.UnitId.HasValue)
            .Select(i => i.ProductId)
            .Distinct()
            .ToList();
        var summaries = await products.ResolveSummariesAsync(candidateIds, ct);
        var trackedProductIds = summaries
            .Where(kv => kv.Value.TrackStock)
            .Select(kv => kv.Key)
            .ToHashSet();

        // Full required target set (all tracked ingredients scaled to servings) via the shared
        // calculator so the button label and the synced set cannot diverge (plantry-gsj).
        var itemsToAdd = RecipeShoppingTargets.All(recipe, trackedProductIds, servings);

        if (itemsToAdd.Count == 0)
            return new AddIngredientsResult.NothingToAdd();

        // Idempotent SYNC (SET, last-press-wins): pressing "Add all" then "Add missing" (or vice-versa)
        // leaves the recipe slice equal to the last button's target — no accumulation (plantry-gsj).
        var outcome = await shoppingWriter.SyncSourceContributionAsync(itemsToAdd, RecipeSource, recipeId.Value, ct);

        return new AddIngredientsResult.Added(itemsToAdd.Count, outcome);
    }
}

// ── Result discriminated union ──────────────────────────────────────────────────────────────────

/// <summary>The outcome of an <see cref="AddIngredientsToShoppingList.ExecuteAsync"/> call.</summary>
public abstract record AddIngredientsResult
{
    private AddIngredientsResult() { }

    /// <summary>The recipe slice was synced to the shopping list.</summary>
    /// <param name="ItemCount">Number of ingredient lines in the synced target set.</param>
    /// <param name="Outcome">Per-target counts (added / already-present / checked-off) for the result summary.</param>
    public sealed record Added(int ItemCount, ShoppingSyncOutcome Outcome) : AddIngredientsResult;

    /// <summary>
    /// No ingredient qualified for the list — every ingredient is either an untracked staple
    /// (null Quantity/UnitId) or a quantity-bearing line whose product is untracked / unknown to
    /// the household (track_stock = false or absent from Catalog).
    /// </summary>
    public sealed record NothingToAdd : AddIngredientsResult;

    /// <summary>The recipe does not exist in this household.</summary>
    public sealed record NotFound : AddIngredientsResult;

    /// <summary>The caller is not authenticated / no household context.</summary>
    public sealed record Unauthorized : AddIngredientsResult;

    /// <summary>Invalid input (e.g. servings &lt; 1).</summary>
    public sealed record Invalid(Error Error) : AddIngredientsResult;
}
