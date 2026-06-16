using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Shopping.Domain;

/// <summary>
/// Aggregate root for the Shopping bounded context (shopping.md, ADR-010).
/// One list per household in v1; the root table and Name column exist so multiple named lists
/// are a non-breaking future change (shopping.md resolved call 1).
/// Mutable working state: items are edited in place and hard-deleted on clear (resolved call 2).
/// </summary>
public sealed class ShoppingList : AggregateRoot<ShoppingListId>
{
    // Backing field for the child collection — wired via EF PropertyAccessMode.Field
    private readonly List<ShoppingListItem> _items = [];

    // EF constructor
    private ShoppingList() { }

    private ShoppingList(ShoppingListId id, HouseholdId householdId, string name, DateTimeOffset now)
    {
        Id = id;
        HouseholdId = householdId;
        Name = name;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Defaults to "Shopping List". Exists to allow multiple named lists in future without migration.</summary>
    public string Name { get; private set; } = "Shopping List";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ShoppingListItem> Items => _items.AsReadOnly();

    /// <summary>
    /// Factory: creates a new ShoppingList for the given household, seeded with name "Shopping List".
    /// </summary>
    public static ShoppingList Create(HouseholdId householdId, IClock clock) =>
        new(ShoppingListId.New(), householdId, "Shopping List", clock.UtcNow);

    /// <summary>
    /// Adds a catalog-backed item to the list. P2-Sb owns the merge-on-duplicate orchestration;
    /// this primitive does a pure add. Use <see cref="FindUncheckedByProduct"/> to check for
    /// an existing item first.
    /// </summary>
    public ShoppingListItem AddItem(
        Guid productId,
        decimal? quantity,
        Guid? unitId,
        string? note,
        ItemSource source,
        Guid? sourceRef,
        IClock clock)
    {
        var item = ShoppingListItem.ForProduct(
            HouseholdId, Id, productId, quantity, unitId, note, source, sourceRef, clock);
        _items.Add(item);
        UpdatedAt = clock.UtcNow;
        return item;
    }

    /// <summary>
    /// Adds a free-text item to the list.
    /// </summary>
    public ShoppingListItem AddFreeTextItem(
        string freeText,
        decimal? quantity,
        Guid? unitId,
        string? note,
        IClock clock)
    {
        var item = ShoppingListItem.ForFreeText(
            HouseholdId, Id, freeText, quantity, unitId, note, clock);
        _items.Add(item);
        UpdatedAt = clock.UtcNow;
        return item;
    }

    /// <summary>
    /// Returns the first unchecked item matching the product, or null. Used by P2-Sb to implement
    /// the duplicate-merge primitive (shopping.md resolved call 5).
    /// </summary>
    public ShoppingListItem? FindUncheckedByProduct(Guid productId) =>
        _items.FirstOrDefault(i => i.ProductId == productId && !i.IsChecked);

    /// <summary>
    /// Merges an incoming quantity into an existing unchecked product line in place
    /// (shopping.md resolved call 5 — duplicate-product merge path). The item must
    /// already belong to this list; use <see cref="FindUncheckedByProduct"/> first.
    /// </summary>
    public void MergeItem(ShoppingListItem existing, decimal? incomingQuantity, Guid? incomingUnitId, IClock clock)
    {
        if (!_items.Contains(existing))
            throw new InvalidOperationException($"Item {existing.Id} does not belong to list {Id}.");
        existing.MergeFrom(incomingQuantity, incomingUnitId, clock);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Checks off an item by ID. Stamps CheckedAt/CheckedBy. Throws if the item is not found.
    /// </summary>
    public void CheckOff(ShoppingListItemId itemId, Guid userId, IClock clock)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Item {itemId} not found on list {Id}.");
        item.CheckOff(userId, clock);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Unchecks a previously checked item by ID. Clears CheckedAt/CheckedBy. Throws if the item is not found.
    /// </summary>
    public void UncheckItem(ShoppingListItemId itemId, IClock clock)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Item {itemId} not found on list {Id}.");
        item.Uncheck(clock);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Hard-removes a single item from the list by ID. Throws if the item is not found.
    /// Distinct from <see cref="ClearChecked"/> — removes any item regardless of checked state.
    /// </summary>
    public void RemoveItem(ShoppingListItemId itemId, IClock clock)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Item {itemId} not found on list {Id}.");
        _items.Remove(item);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Edits the quantity and unit of a single item in place (inline qty/unit editor, plantry-dem).
    /// Quantity may be null (clears the quantity). UnitId may be null (no unit).
    /// Throws if the item is not found on this list.
    /// </summary>
    public void EditItemQuantity(ShoppingListItemId itemId, decimal? quantity, Guid? unitId, IClock clock)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Item {itemId} not found on list {Id}.");
        item.EditQuantity(quantity, unitId, clock);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Sets or clears the note on a single item (inline note editor, plantry-dem).
    /// Null or whitespace-only note clears the note field.
    /// Throws if the item is not found on this list.
    /// </summary>
    public void SetItemNote(ShoppingListItemId itemId, string? note, IClock clock)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Item {itemId} not found on list {Id}.");
        item.SetNote(note, clock);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Assigns or clears a category on a single item (recategorize action, plantry-259).
    /// Primarily meaningful for free-text items that have no catalog product driving their category.
    /// Throws if the item is not found on this list.
    /// </summary>
    public void SetItemCategory(ShoppingListItemId itemId, Guid? categoryId, IClock clock)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Item {itemId} not found on list {Id}.");
        item.SetCategory(categoryId, clock);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Hard-removes all checked items from the list (SPEC §3e, shopping.md resolved call 2).
    /// </summary>
    public IReadOnlyList<ShoppingListItem> ClearChecked(IClock clock)
    {
        var cleared = _items.Where(i => i.IsChecked).ToList();
        foreach (var item in cleared)
            _items.Remove(item);
        if (cleared.Count > 0)
            UpdatedAt = clock.UtcNow;
        return cleared;
    }
}
