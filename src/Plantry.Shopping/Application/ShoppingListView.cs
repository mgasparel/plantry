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
    /// <summary>Hue in degrees (0–359) on the oklch colour wheel, inherited from the product's category. Null when uncategorised or category has no hue (renders neutral chip).</summary>
    int? CategoryHue,
    /// <summary>Optional item note, persisted on the domain entity and surfaced for the note sub-line in the UI.</summary>
    string? Note,
    bool IsChecked,
    System.DateTimeOffset? CheckedAt,
    System.DateTimeOffset CreatedAt,
    /// <summary>
    /// On-hand quantity in the product's display unit, enriched via <see cref="IShoppingPantryReader"/>.
    /// Null for free-text items (no product id) or when the product has no stock record in Inventory.
    /// </summary>
    decimal? OnHand = null,
    /// <summary>
    /// Display unit code for the on-hand quantity (e.g. "g", "ea"). Null when <see cref="OnHand"/> is null.
    /// </summary>
    string? PantryUnitCode = null,
    /// <summary>
    /// True when the pantry stock is at or below par (or zero). Null when <see cref="OnHand"/> is null.
    /// Shopping renders this as the "· low" warning sub-line.
    /// </summary>
    bool? IsLow = null)
{
    /// <summary>Display label — product name or free-text, never null for a well-formed item.</summary>
    public string DisplayName => ProductName ?? FreeText ?? "(unnamed)";

    /// <summary>
    /// True when pantry stock data is available for this item (i.e. it is a product-backed item
    /// with a stock record in Inventory). Drives the .sl-instock sub-line visibility.
    /// </summary>
    public bool HasPantryStock => OnHand.HasValue;
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

/// <summary>
/// One chip in the "Running low in your pantry" suggestions strip (plantry-48l).
/// Represents a pantry product that is at or below par and NOT already on the active shopping list.
/// Resolved by the page model by joining low-stock products from <see cref="IShoppingPantryReader"/>
/// with catalog summaries from <see cref="IShoppingCatalogReader"/>.
/// </summary>
/// <param name="ProductId">Catalog product id — used as the hidden input value for the one-click add.</param>
/// <param name="Name">Product display name.</param>
/// <param name="OnHand">On-hand quantity in <see cref="UnitCode"/> units. Zero means "out of stock".</param>
/// <param name="UnitCode">Display unit code (e.g. "g", "ea").</param>
/// <param name="IsLow">True when on-hand is at or below par (always true for suggestions).</param>
/// <param name="CategoryName">Category name for the colour chip. Null when uncategorised.</param>
/// <param name="CategoryHue">Hue in degrees (0–359) for the colour dot. Null when uncategorised.</param>
public sealed record PantrySuggestion(
    Guid ProductId,
    string Name,
    decimal OnHand,
    string UnitCode,
    bool IsLow,
    string? CategoryName,
    int? CategoryHue)
{
    /// <summary>Label shown next to the chip: "N unit" or "out" when zero.</summary>
    public string StockLabel => OnHand > 0
        ? $"{OnHand:0.###} {UnitCode} left"
        : "out";
}
