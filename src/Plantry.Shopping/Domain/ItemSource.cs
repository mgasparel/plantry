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

/// <summary>
/// Outcome of a <see cref="ShoppingListItem.SetContribution"/> SET (plantry-gsj): how the source's
/// slice changed. Drives the sync result summary — Created/Increased count as "added", Unchanged/Reduced
/// as "already on your list".
/// </summary>
public enum ContributionChange
{
    /// <summary>No contribution for this source existed; a fresh one was created.</summary>
    Created,

    /// <summary>An existing contribution's quantity grew to meet the new target.</summary>
    Increased,

    /// <summary>An existing contribution already covered the target — no change.</summary>
    Unchanged,

    /// <summary>An existing contribution's quantity shrank (e.g. servings reduced).</summary>
    Reduced,
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
