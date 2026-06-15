namespace Plantry.Shopping.Domain;

/// <summary>
/// Provenance of a <see cref="ShoppingListItem"/>: where the item was added from.
/// The CHECK constraint in the migration enforces this closed set at the DB layer (shopping.md).
/// </summary>
public enum ItemSource
{
    Manual,
    Recipe,
    MealPlan,
    Deal,
}

public static class ItemSourceExtensions
{
    public static string ToDbValue(this ItemSource source) => source switch
    {
        ItemSource.Manual   => "manual",
        ItemSource.Recipe   => "recipe",
        ItemSource.MealPlan => "meal_plan",
        ItemSource.Deal     => "deal",
        _                   => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    public static ItemSource Parse(string value) => value switch
    {
        "manual"    => ItemSource.Manual,
        "recipe"    => ItemSource.Recipe,
        "meal_plan" => ItemSource.MealPlan,
        "deal"      => ItemSource.Deal,
        _           => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown ItemSource"),
    };
}
