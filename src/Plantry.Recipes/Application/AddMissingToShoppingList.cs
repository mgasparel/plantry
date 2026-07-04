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
///   <item>Takes Missing AND Low lines (not InStock or Untracked — C12 / recipes-journeys.md J5).</item>
///   <item>Emits the per-line shortfall (scaled required minus available) — what the household
///     still needs to buy. For Missing lines available is 0, so shortfall equals the full scaled
///     quantity. Uses <see cref="RecipeShortfallCalculator"/> so the logic is shared with the
///     ShopForWeek path (J6) and cannot drift.</item>
///   <item>Calls <see cref="IShoppingListWriter.AddItemsAsync"/> with those lines,
///     <c>source = "recipe"</c>, and <c>sourceRef = recipeId</c>.</item>
/// </list>
/// </para>
/// <para>Merge/no-dup logic is Shopping's concern (DM-18, shopping.md resolved call 5) — this
/// service passes all shortfall lines without pre-filtering for existing shopping-list entries.
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

        // Compute the "Add missing" target set (Missing + Low shortfall lines) via the shared
        // target calculator so the button label and the synced set cannot diverge (plantry-gsj).
        var itemsToAdd = RecipeShoppingTargets.Missing(recipe, fulfillment, desiredServings);

        // Nothing to buy: a no-op, NOT a reconcile-away. The button is hidden when there is no
        // shortfall, so an empty target here must not strip an existing recipe slice (plantry-gsj).
        if (itemsToAdd.Count == 0)
            return new AddMissingResult.NothingMissing();

        // Idempotent SYNC: SET the recipe's own slice to exactly this shortfall (no drift on re-press),
        // reconciling away any in-stock products a prior "Add all" contributed (last-press-wins).
        var outcome = await shoppingWriter.SyncSourceContributionAsync(itemsToAdd, RecipeSource, recipeId.Value, ct);

        return new AddMissingResult.Added(itemsToAdd.Count, outcome);
    }
}

// ── Result discriminated union ──────────────────────────────────────────────────────────────────

/// <summary>The outcome of an <see cref="AddMissingToShoppingList.ExecuteAsync"/> call.</summary>
public abstract record AddMissingResult
{
    private AddMissingResult() { }

    /// <summary>The recipe slice was synced to the shopping list.</summary>
    /// <param name="ItemCount">Number of shortfall lines in the synced target set.</param>
    /// <param name="Outcome">Per-target counts (added / already-present / checked-off) for the result summary.</param>
    public sealed record Added(int ItemCount, ShoppingSyncOutcome Outcome) : AddMissingResult;

    /// <summary>No tracked ingredient has a positive shortfall (all are InStock, or Low with sufficient stock to cover the need, or Untracked).</summary>
    public sealed record NothingMissing : AddMissingResult;

    /// <summary>The recipe does not exist in this household.</summary>
    public sealed record NotFound : AddMissingResult;

    /// <summary>The caller is not authenticated / no household context.</summary>
    public sealed record Unauthorized : AddMissingResult;

    /// <summary>Invalid input (e.g. desiredServings &lt; 1).</summary>
    public sealed record Invalid(Error Error) : AddMissingResult;
}
