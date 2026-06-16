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
    ITenantContext tenant) : IShoppingListWriter
{
    public async Task AddItemsAsync(
        IEnumerable<ShoppingItem> items,
        string source,
        Guid sourceRef,
        CancellationToken ct = default)
    {
        var itemSource = ParseSource(source);

        foreach (var item in items)
        {
            var command = new AddItemCommand(
                productId: item.ProductId,
                freeText: null,
                quantity: item.Quantity,
                unitId: item.UnitId,
                note: null,
                source: itemSource,
                sourceRef: sourceRef,
                intentionalDuplicate: false,
                repository: repository,
                catalogReader: catalogReader,
                clock: clock,
                tenant: tenant);

            // On failure (e.g. no shopping list for the household), abort — the caller should not
            // partially add items if the list cannot be found. A missing list is a setup/seeding
            // problem, not an expected runtime condition.
            var result = await command.ExecuteAsync(ct);
            if (result.IsFailure)
                throw new InvalidOperationException(
                    $"ShoppingListWriterAdapter.AddItemsAsync failed for product {item.ProductId}: " +
                    $"{result.Error.Code} — {result.Error.Description}");
        }
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
