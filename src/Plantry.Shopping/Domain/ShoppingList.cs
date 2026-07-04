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
    /// Adds a fresh catalog-backed item to the list and immediately adds the initial contribution.
    /// Only called when no unchecked item for this product exists (or intentional duplicate).
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
        var item = ShoppingListItem.ForProduct(HouseholdId, Id, productId, unitId, note, clock);
        // Seed the first contribution for this item.
        item.UpsertContribution(source, sourceRef, quantity, unitId, clock);
        _items.Add(item);
        UpdatedAt = clock.UtcNow;
        return item;
    }

    /// <summary>
    /// Adds a free-text item to the list. Free-text items are always inserted unconditionally;
    /// they receive exactly one Manual contribution.
    /// </summary>
    public ShoppingListItem AddFreeTextItem(
        string freeText,
        decimal? quantity,
        Guid? unitId,
        string? note,
        IClock clock)
    {
        var item = ShoppingListItem.ForFreeText(HouseholdId, Id, freeText, quantity, unitId, note, clock);
        _items.Add(item);
        UpdatedAt = clock.UtcNow;
        return item;
    }

    /// <summary>
    /// Returns the first unchecked item matching the product, or null. Used by
    /// <see cref="Plantry.Shopping.Application.AddItemCommand"/> to implement the
    /// per-source upsert when an existing item row exists for this product.
    /// </summary>
    public ShoppingListItem? FindUncheckedByProduct(Guid productId) =>
        _items.FirstOrDefault(i => i.ProductId == productId && !i.IsChecked);

    /// <summary>
    /// Returns the first unchecked item for <paramref name="productId"/> that already carries a
    /// contribution for the given (source, sourceRef), or null (plantry-gsj). The sync verb prefers
    /// the row where this source's slice already lives so re-syncing a unit-split product stays
    /// idempotent (it sets the existing slice rather than spawning a second row each press).
    /// </summary>
    public ShoppingListItem? FindUncheckedRowForSource(Guid productId, ItemSource source, Guid? sourceRef) =>
        _items.FirstOrDefault(i =>
            i.ProductId == productId && !i.IsChecked && i.FindContribution(source, sourceRef) is not null);

    /// <summary>True when the list has a checked-off item for the product (any source) — a completed
    /// intent the sync must not resurrect (plantry-gsj).</summary>
    public bool HasCheckedItemForProduct(Guid productId) =>
        _items.Any(i => i.ProductId == productId && i.IsChecked);

    /// <summary>
    /// Upserts a per-source contribution on an existing item using (Source, SourceRef) as the match key.
    ///
    /// <para><b>New (Source, SourceRef) pair:</b> adds a new contribution with <paramref name="incomingQuantity"/>.</para>
    /// <para><b>Existing key:</b> tops up that contribution by delta = max(0, incomingQuantity − current).
    ///   A no-op when the need is already covered.</para>
    ///
    /// The caller (<see cref="Plantry.Shopping.Application.AddItemCommand"/>) is responsible for
    /// resolving unit compatibility before calling here — only same-unit adds reach this path;
    /// unit-incompatible adds insert a second item row instead.
    ///
    /// Called exclusively by <see cref="Plantry.Shopping.Application.AddItemCommand"/> (plantry-9scq).
    /// </summary>
    public void UpsertContribution(
        ShoppingListItem existing,
        ItemSource source,
        Guid? sourceRef,
        decimal? incomingQuantity,
        Guid? incomingUnitId,
        IClock clock)
    {
        if (!_items.Contains(existing))
            throw new InvalidOperationException($"Item {existing.Id} does not belong to list {Id}.");

        var existingContrib = existing.FindContribution(source, sourceRef);
        decimal? delta;
        if (existingContrib is null)
        {
            // New source/sourceRef key — add a fresh contribution with the full incoming quantity.
            delta = incomingQuantity;
        }
        else
        {
            // Existing key — compute per-source top-up delta:
            // max(0, incoming − that source's current quantity).
            delta = ComputeToAdd(incomingQuantity, existingContrib.Quantity);
        }

        existing.UpsertContribution(source, sourceRef, delta, incomingUnitId, clock);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Idempotently SETS a source's contribution on an existing item to an absolute quantity
    /// (plantry-gsj) — the SYNC verb driving "Add missing"/"Add all". Unlike
    /// <see cref="UpsertContribution"/> (additive top-up), this REPLACES the source's slice, so
    /// re-syncing the same target is a no-op and a servings change re-sets the slice rather than
    /// stacking. Other sources' slices are untouched (plantry-9scq sums / plantry-26g attribution
    /// preserved).
    ///
    /// <para>The caller (<see cref="Plantry.Shopping.Application.SyncSourceContributionCommand"/>)
    /// resolves unit compatibility before calling here — only same-unit sets reach this path.</para>
    /// </summary>
    /// <returns>How the slice changed (created / grown / unchanged / reduced).</returns>
    public ContributionChange SetSourceContribution(
        ShoppingListItem existing,
        ItemSource source,
        Guid? sourceRef,
        decimal? quantity,
        Guid? incomingUnitId,
        IClock clock)
    {
        if (!_items.Contains(existing))
            throw new InvalidOperationException($"Item {existing.Id} does not belong to list {Id}.");

        var change = existing.SetContribution(source, sourceRef, quantity, incomingUnitId, clock);
        UpdatedAt = clock.UtcNow;
        return change;
    }

    /// <summary>
    /// Removes a source's contribution from an existing item as part of whole-slice reconciliation
    /// (plantry-gsj): when a sync no longer targets a product, that source's slice is dropped. If the
    /// item is left with no contributions at all, the item row is removed from the list entirely.
    /// Other sources' contributions keep the row alive.
    /// </summary>
    /// <returns>True if a contribution was removed; false if the item carried none for this source.</returns>
    public bool RemoveSourceContribution(
        ShoppingListItem existing,
        ItemSource source,
        Guid? sourceRef,
        IClock clock)
    {
        if (!_items.Contains(existing))
            throw new InvalidOperationException($"Item {existing.Id} does not belong to list {Id}.");

        var removed = existing.RemoveContribution(source, sourceRef, clock);
        if (removed && !existing.HasContributions)
            _items.Remove(existing);

        if (removed)
            UpdatedAt = clock.UtcNow;
        return removed;
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

    /// <summary>
    /// Computes the per-source top-up delta: max(0, incoming − alreadyFromThisSource).
    /// Returns null when incoming is null (nothing to add).
    /// </summary>
    private static decimal? ComputeToAdd(decimal? incoming, decimal? alreadyFromThisSource)
    {
        if (!incoming.HasValue)
            return null;
        var already = alreadyFromThisSource ?? 0m;
        var delta = incoming.Value - already;
        return delta > 0m ? delta : (decimal?)null;
    }
}
