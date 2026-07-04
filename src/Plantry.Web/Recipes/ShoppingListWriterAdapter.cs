using Microsoft.Extensions.Logging;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IShoppingListWriter"/> — delegates each item in the batch to
/// Shopping's <see cref="AddItemCommand"/>, stamping <c>source = Recipe</c> and
/// <c>sourceRef = recipeId</c> uniformly (DM-18, shopping.md §3d / resolved call 5).
///
/// <para>The merge rule (increment quantity for an unchecked item with the same product rather than
/// inserting a duplicate) is enforced by <see cref="AddItemCommand"/> with
/// <c>intentionalDuplicate = false</c>. This adapter does not pre-filter or deduplicate — that is
/// Shopping's responsibility (recipes-domain-model.md §7 "Merge/no-dup is Shopping's concern").</para>
///
/// <para>Lives in Plantry.Web, the composition root that references both Recipes and Shopping, so
/// the Recipes projects keep their <c>→ SharedKernel only</c> dependency.</para>
/// </summary>
public sealed class ShoppingListWriterAdapter(
    IShoppingListRepository repository,
    IShoppingCatalogReader catalogReader,
    IClock clock,
    ITenantContext tenant,
    ILogger<SyncSourceContributionCommand>? syncLogger = null) : IShoppingListWriter
{
    public async Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
        IReadOnlyList<ShoppingItem> items,
        string source,
        Guid sourceRef,
        CancellationToken ct = default)
    {
        var itemSource = ParseSource(source);

        var command = new SyncSourceContributionCommand(
            items: items.Select(i => new SyncItem(i.ProductId, i.Quantity, i.UnitId)).ToList(),
            source: itemSource,
            sourceRef: sourceRef,
            repository: repository,
            catalogReader: catalogReader,
            clock: clock,
            tenant: tenant,
            logger: syncLogger);

        // On failure (e.g. no shopping list for the household), abort — a missing list is a
        // setup/seeding problem, not an expected runtime condition.
        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"ShoppingListWriterAdapter.SyncSourceContributionAsync failed for source '{source}' ref {sourceRef}: " +
                $"{result.Error.Code} — {result.Error.Description}");

        var outcome = result.Value;
        return new ShoppingSyncOutcome(outcome.Added, outcome.AlreadyPresent, outcome.CheckedOff);
    }

    private static ItemSource ParseSource(string source) => source switch
    {
        "recipe"    => ItemSource.Recipe,
        "meal_plan" => ItemSource.MealPlan,
        "deal"      => ItemSource.Deal,
        "manual"    => ItemSource.Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(source),
            $"Unknown shopping item source: '{source}'"),
    };
}
