using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Shopping.Domain;

/// <summary>
/// Child entity of the <see cref="ShoppingList"/> aggregate.
/// Invariant: exactly one of <see cref="ProductId"/> / <see cref="FreeText"/> is non-null
/// (enforced by the domain factory and by CHECK num_nonnulls(product_id, free_text) = 1 in the DB).
/// Mutable working state — items are edited in place and hard-deleted on clear (shopping.md, resolved call 2).
/// </summary>
public sealed class ShoppingListItem : Entity<ShoppingListItemId>
{
    // EF constructor
    private ShoppingListItem() { }

    private ShoppingListItem(
        ShoppingListItemId id,
        HouseholdId householdId,
        ShoppingListId shoppingListId,
        Guid? productId,
        string? freeText,
        decimal? quantity,
        Guid? unitId,
        string? note,
        ItemSource source,
        Guid? sourceRef,
        DateTimeOffset createdAt)
    {
        Id = id;
        HouseholdId = householdId;
        ShoppingListId = shoppingListId;
        ProductId = productId;
        FreeText = freeText;
        Quantity = quantity;
        UnitId = unitId;
        Note = note;
        Source = source;
        SourceRef = sourceRef;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public HouseholdId HouseholdId { get; private set; }
    public ShoppingListId ShoppingListId { get; private set; }

    /// <summary>Soft ref → catalog.product. Null when FreeText is set.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>For non-catalog items. Null when ProductId is set.</summary>
    public string? FreeText { get; private set; }

    public decimal? Quantity { get; private set; }

    /// <summary>Soft ref → catalog.unit.</summary>
    public Guid? UnitId { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Null = unchecked. Drives checked-to-bottom ordering and "clear checked" (SPEC §3c/§3e).</summary>
    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>Attribution — soft ref → identity user.</summary>
    public Guid? CheckedBy { get; private set; }

    /// <summary>
    /// Soft ref → catalog.category. Set by <see cref="SetCategory"/> (recategorize action).
    /// Null for product-backed items (their category is derived from the product's catalog entry)
    /// and for uncategorized free-text items. Only meaningful when <see cref="FreeText"/> is set.
    /// </summary>
    public Guid? CategoryId { get; private set; }

    public ItemSource Source { get; private set; }

    /// <summary>Soft ref to the originating recipe / meal_plan / deal.</summary>
    public Guid? SourceRef { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsChecked => CheckedAt.HasValue;

    /// <summary>
    /// Creates a new catalog-backed item (exactly one of productId / freeText: productId path).
    /// </summary>
    internal static ShoppingListItem ForProduct(
        HouseholdId householdId,
        ShoppingListId shoppingListId,
        Guid productId,
        decimal? quantity,
        Guid? unitId,
        string? note,
        ItemSource source,
        Guid? sourceRef,
        IClock clock)
    {
        return new ShoppingListItem(
            ShoppingListItemId.New(),
            householdId,
            shoppingListId,
            productId: productId,
            freeText: null,
            quantity: quantity,
            unitId: unitId,
            note: note,
            source: source,
            sourceRef: sourceRef,
            createdAt: clock.UtcNow);
    }

    /// <summary>
    /// Creates a new free-text item (exactly one of productId / freeText: freeText path).
    /// </summary>
    internal static ShoppingListItem ForFreeText(
        HouseholdId householdId,
        ShoppingListId shoppingListId,
        string freeText,
        decimal? quantity,
        Guid? unitId,
        string? note,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(freeText))
            throw new ArgumentException("FreeText may not be blank.", nameof(freeText));

        return new ShoppingListItem(
            ShoppingListItemId.New(),
            householdId,
            shoppingListId,
            productId: null,
            freeText: freeText,
            quantity: quantity,
            unitId: unitId,
            note: note,
            source: ItemSource.Manual,
            sourceRef: null,
            createdAt: clock.UtcNow);
    }

    /// <summary>
    /// Sets CheckedAt and CheckedBy. Idempotent if already checked (re-stamps the time).
    /// </summary>
    internal void CheckOff(Guid userId, IClock clock)
    {
        CheckedAt = clock.UtcNow;
        CheckedBy = userId;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Clears CheckedAt and CheckedBy, returning the item to unchecked state.
    /// Idempotent if already unchecked.
    /// </summary>
    internal void Uncheck(IClock clock)
    {
        CheckedAt = null;
        CheckedBy = null;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Merges an incoming duplicate-product add into this existing unchecked item:
    /// increments Quantity by <paramref name="incomingQuantity"/> when both are non-null,
    /// replaces when only incoming is non-null, and leaves it unchanged when incoming is null.
    /// Also adopts the incoming unitId if provided and the current item has none.
    /// Called exclusively by <see cref="ShoppingList.MergeItem"/> (shopping.md resolved call 5).
    /// </summary>
    internal void MergeFrom(decimal? incomingQuantity, Guid? incomingUnitId, IClock clock)
    {
        if (incomingQuantity.HasValue)
        {
            Quantity = Quantity.HasValue
                ? Quantity.Value + incomingQuantity.Value
                : incomingQuantity.Value;
        }

        if (incomingUnitId.HasValue && UnitId is null)
            UnitId = incomingUnitId;

        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Sets the quantity and unit on the item (inline qty/unit editor, plantry-dem).
    /// Quantity may be null (clears the quantity). UnitId may be null (no unit).
    /// Called exclusively by <see cref="ShoppingList.EditItemQuantity"/>.
    /// </summary>
    internal void EditQuantity(decimal? quantity, Guid? unitId, IClock clock)
    {
        Quantity = quantity;
        UnitId = unitId;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Sets or clears the note on the item (inline note editor, plantry-dem).
    /// Null or whitespace-only note is stored as null (no note).
    /// Called exclusively by <see cref="ShoppingList.SetItemNote"/>.
    /// </summary>
    internal void SetNote(string? note, IClock clock)
    {
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Assigns a category to this item (recategorize action, plantry-259).
    /// Primarily meaningful for free-text items that have no catalog product driving their category.
    /// Setting a non-null <paramref name="categoryId"/> places the item in the named category group
    /// when the shopping list is re-queried. Passing null clears the assignment (moves item back to Uncategorized).
    /// Called exclusively by <see cref="ShoppingList.SetItemCategory"/>.
    /// </summary>
    internal void SetCategory(Guid? categoryId, IClock clock)
    {
        CategoryId = categoryId;
        UpdatedAt = clock.UtcNow;
    }
}
