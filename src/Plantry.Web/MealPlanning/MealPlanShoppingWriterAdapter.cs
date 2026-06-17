using Plantry.MealPlanning.Application;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IMealPlanShoppingWriter"/> — delegates to the existing
/// <see cref="Recipes.ShoppingListWriterAdapter"/> via Shopping's <see cref="AddItemCommand"/>.
/// Translates <see cref="MealPlanShoppingItem"/>s into Shopping's item type and applies the
/// same merge rule (DM-18) used by the Recipes "add missing" flow.
/// Lives in Plantry.Web (the composition root) to keep MealPlanning free of Shopping/Recipes deps.
/// </summary>
public sealed class MealPlanShoppingWriterAdapter(
    IShoppingListRepository repository,
    IShoppingCatalogReader catalogReader,
    IClock clock,
    ITenantContext tenant) : IMealPlanShoppingWriter
{
    public async Task AddItemsAsync(
        IEnumerable<MealPlanShoppingItem> items,
        string source,
        Guid sourceRef,
        CancellationToken ct = default)
    {
        var itemSource = source switch
        {
            "meal_plan" => Plantry.Shopping.Domain.ItemSource.MealPlan,
            "recipe"    => Plantry.Shopping.Domain.ItemSource.Recipe,
            "manual"    => Plantry.Shopping.Domain.ItemSource.Manual,
            _ => throw new ArgumentOutOfRangeException(nameof(source), $"Unknown source: '{source}'"),
        };

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

            var result = await command.ExecuteAsync(ct);
            if (result.IsFailure)
                throw new InvalidOperationException(
                    $"MealPlanShoppingWriterAdapter.AddItemsAsync failed for product {item.ProductId}: " +
                    $"{result.Error.Code} — {result.Error.Description}");
        }
    }
}
