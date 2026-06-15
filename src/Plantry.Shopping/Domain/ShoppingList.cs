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
