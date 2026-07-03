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
    bool? IsLow = null,
    /// <summary>
    /// Resolved attribution labels for this item's contributions, rendered as the source sub-line on the board.
    /// Each label carries a structural <see cref="AttributionKind"/> the UI keys presentation off (e.g. the
    /// recipe icon), plus its display <c>Text</c> — presentation never sniffs the wording.
    ///
    /// <para>
    /// Built by <see cref="ShoppingListQueryService"/> from the item's <c>Contributions</c> collection:
    /// <list type="bullet">
    ///   <item><description>Recipe contributions → <c>(Recipe, "for {RecipeName}")</c>.</description></item>
    ///   <item><description>A Recipe that appears more than once (distinct SourceRef, same name) → <c>(Recipe, "for {RecipeName} ×N")</c>.</description></item>
    ///   <item><description>Manual contributions → <c>(Manual, "added by you")</c>.</description></item>
    ///   <item><description>MealPlan/Deal → omitted (future ports; no label yet).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>Empty when the item has no resolvable attribution (e.g. all contributions are MealPlan/Deal
    /// which have no resolution port yet, or there are no contributions).</para>
    /// </summary>
    IReadOnlyList<AttributionLabel>? AttributionLabels = null,
    /// <summary>
    /// Resolved store name for the product's cheapest active deal (P5-9), read at request time from
    /// <b>Pricing</b>'s cheapest-active-deal read model via <see cref="IShoppingDealReader"/> — never
    /// stored (ADR-010 R3/D11). Drives the "On sale at {store} this week" badge. Null when the product has
    /// no active deal, or when a deal is active but its store is unresolved (badge falls back to "On sale
    /// this week"). Presence of a badge is governed by <see cref="DealId"/>, not this name.
    /// </summary>
    string? DealStoreName = null,
    /// <summary>
    /// The deal behind the active-deal badge (Pricing observation source ref) — provenance for the
    /// tappable link. Non-null exactly when the product has a cheapest active deal. Null otherwise.
    /// </summary>
    Guid? DealId = null)
{
    /// <summary>Display label — product name or free-text, never null for a well-formed item.</summary>
    public string DisplayName => ProductName ?? FreeText ?? "(unnamed)";

    /// <summary>
    /// True when the product has a cheapest active deal to badge ("On sale at {store} this week").
    /// Keyed on <see cref="DealId"/> so a deal with an unresolved store still badges (storeless text).
    /// </summary>
    public bool HasDeal => DealId.HasValue;

    /// <summary>
    /// True when pantry stock data is available for this item (i.e. it is a product-backed item
    /// with a stock record in Inventory). Drives the .sl-instock sub-line visibility.
    /// </summary>
    public bool HasPantryStock => OnHand.HasValue;

    /// <summary>
    /// True when there are resolved attribution labels to render on the source sub-line.
    /// </summary>
    public bool HasAttribution => AttributionLabels is { Count: > 0 };
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
