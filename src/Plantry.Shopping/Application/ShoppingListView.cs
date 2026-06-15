namespace Plantry.Shopping.Application;

/// <summary>
/// Read model for one shopping list item, enriched at read time with catalog data
/// (product name, category name, unit code) resolved via <see cref="IShoppingCatalogReader"/>.
/// </summary>
public sealed record ShoppingListItemView(
    Guid ItemId,
    Guid ListId,
    Guid? ProductId,
    string? ProductName,
    string? FreeText,
    decimal? Quantity,
    Guid? UnitId,
    string? UnitCode,
    string? CategoryName,
    bool IsChecked,
    System.DateTimeOffset? CheckedAt,
    System.DateTimeOffset CreatedAt)
{
    /// <summary>Display label — product name or free-text, never null for a well-formed item.</summary>
    public string DisplayName => ProductName ?? FreeText ?? "(unnamed)";
}

/// <summary>
/// Read model for the full shopping list grouped by category.
/// Only unchecked items appear in <see cref="Groups"/> and <see cref="UncategorizedItems"/>.
/// Checked items are collected separately in <see cref="CheckedItems"/> so the UI can render
/// them at the bottom regardless of their category, satisfying SPEC §3c "checked items sink
/// to the bottom (struck-through)".
/// Free-text items (no product or category) land in <see cref="UncategorizedItems"/>.
/// </summary>
public sealed record ShoppingListView(
    Guid ListId,
    string ListName,
    IReadOnlyList<ShoppingCategoryGroup> Groups,
    IReadOnlyList<ShoppingListItemView> UncategorizedItems,
    IReadOnlyList<ShoppingListItemView> CheckedItems,
    int TotalCount,
    int CheckedCount);

/// <summary>One category bucket in the grouped shopping list (unchecked items only).</summary>
public sealed record ShoppingCategoryGroup(
    string CategoryName,
    IReadOnlyList<ShoppingListItemView> Items);
