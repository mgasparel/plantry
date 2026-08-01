using Plantry.MealPlanning.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Server-authoritative validation and normalization for product dishes.
/// This is deliberately non-persisting so both the assignment command and the rollup preview
/// enforce the same product existence, plannability, reachability, and quantity rules.
/// </summary>
public static class ProductDishValidator
{
    public static async Task<IReadOnlyList<DishSpec>> NormalizeAsync(
        IMealPlanCatalogProductReader catalogReader,
        IReadOnlyList<DishSpec> dishes,
        CancellationToken ct = default)
    {
        var normalized = new List<DishSpec>(dishes.Count);
        foreach (var dish in dishes)
        {
            if (dish.Kind == DishKind.Recipe)
            {
                normalized.Add(DishSpec.ForRecipe(dish.ItemId, dish.RequiredServings()));
                continue;
            }

            normalized.Add(await ValidateProductAsync(catalogReader, dish, ct));
        }

        return normalized;
    }

    private static async Task<DishSpec> ValidateProductAsync(
        IMealPlanCatalogProductReader catalogReader,
        DishSpec dish,
        CancellationToken ct)
    {
        if (dish.ItemId == Guid.Empty)
            throw new InvalidOperationException("A product is required.");
        if (dish.Quantity is not > 0m)
            throw new InvalidOperationException("Product quantity must be greater than zero.");
        if (dish.UnitId is not { } unitId || unitId == Guid.Empty)
            throw new InvalidOperationException("A product dish unit is required.");

        if (!await catalogReader.IsPlannableAsync(dish.ItemId, ct))
        {
            var exists = await catalogReader.ExistsAsync(dish.ItemId, ct);
            throw new InvalidOperationException(exists
                ? $"Product {dish.ItemId} is a parent product group and cannot be planned as a dish directly — choose a specific variant."
                : $"Product {dish.ItemId} does not exist in the catalog.");
        }

        var info = await catalogReader.GetPlanningInfoAsync(dish.ItemId, ct)
            ?? throw new InvalidOperationException("Product planning metadata is unavailable.");

        var option = info.UnitOptions.FirstOrDefault(o => o.UnitId == unitId)
            ?? throw new InvalidOperationException("The selected product unit is not reachable for this product.");

        if (string.Equals(option.Dimension, "count", StringComparison.OrdinalIgnoreCase)
            && dish.Quantity.Value != decimal.Truncate(dish.Quantity.Value))
            throw new InvalidOperationException("Count-unit quantities must be whole numbers.");

        return DishSpec.ForProduct(dish.ItemId, dish.Quantity.Value, unitId);
    }
}
