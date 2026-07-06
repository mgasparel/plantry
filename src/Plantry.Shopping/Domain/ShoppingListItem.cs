using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Shopping.Domain;

/// <summary>
/// Child entity of the <see cref="ShoppingList"/> aggregate.
/// Invariant: exactly one of <see cref="ProductId"/> / <see cref="FreeText"/> is non-null
/// (enforced by the domain factory and by CHECK num_nonnulls(product_id, free_text) = 1 in the DB).
/// Mutable working state — items are edited in place and hard-deleted on clear (shopping.md, resolved call 2).
///
/// <para>
/// <b>Per-source contribution model (plantry-9scq):</b> the item holds a collection of
/// <see cref="ShoppingListItemContribution"/> children, one per distinct (Source, SourceRef) pair.
/// <see cref="Quantity"/> is derived as the SUM of all contributions; it is not stored independently.
/// <see cref="UnitId"/> is the canonical unit shared by all contributions on this item row;
/// unit-incompatible adds produce a separate item row rather than mixing incompatible quantities.
/// </para>
/// </summary>
public sealed class ShoppingListItem : Entity<ShoppingListItemId>
{
    // Backing field for the contribution collection — wired via EF PropertyAccessMode.Field.
    private readonly List<ShoppingListItemContribution> _contributions = [];

    // EF constructor
    private ShoppingListItem() { }

    private ShoppingListItem(
        ShoppingListItemId id,
        HouseholdId householdId,
        ShoppingListId shoppingListId,
        Guid? productId,
        string? freeText,
        Guid? unitId,
        string? note,
        DateTimeOffset createdAt)
    {
        Id = id;
        HouseholdId = householdId;
        ShoppingListId = shoppingListId;
        ProductId = productId;
        FreeText = freeText;
        UnitId = unitId;
        Note = note;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public HouseholdId HouseholdId { get; private set; }
    public ShoppingListId ShoppingListId { get; private set; }

    /// <summary>Soft ref → catalog.product. Null when FreeText is set.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>For non-catalog items. Null when ProductId is set.</summary>
    public string? FreeText { get; private set; }

    /// <summary>
    /// Derived total quantity: sum of all contribution quantities.
    /// Returns null when no contribution has a quantity set.
    /// This value is NOT stored — it is always computed from <see cref="Contributions"/>.
    /// </summary>
    public decimal? Quantity => _contributions.Any(c => c.Quantity.HasValue)
        ? _contributions.Sum(c => c.Quantity ?? 0m)
        : (decimal?)null;

    /// <summary>
    /// Canonical unit shared by all contributions on this item row.
    /// Soft ref → catalog.unit. Null when unitless.
    /// All contributions must be expressed in this unit so the sum is meaningful.
    /// </summary>
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

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsChecked => CheckedAt.HasValue;

    /// <summary>Per-source quantity contributions that sum to <see cref="Quantity"/>.</summary>
    public IReadOnlyList<ShoppingListItemContribution> Contributions => _contributions.AsReadOnly();

    /// <summary>
    /// Creates a new catalog-backed item (productId path) with no contributions yet.
    /// The caller must immediately follow up with <see cref="UpsertContribution"/> to record the first source.
    /// </summary>
    internal static ShoppingListItem ForProduct(
        HouseholdId householdId,
        ShoppingListId shoppingListId,
        Guid productId,
        Guid? unitId,
        string? note,
        IClock clock)
    {
        return new ShoppingListItem(
            ShoppingListItemId.New(),
            householdId,
            shoppingListId,
            productId: productId,
            freeText: null,
            unitId: unitId,
            note: note,
            createdAt: clock.UtcNow);
    }

    /// <summary>
    /// Creates a new free-text item (freeText path) with a single Manual contribution seeded immediately.
    /// Free-text items are always inserted unconditionally (no per-source merge).
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

        var item = new ShoppingListItem(
            ShoppingListItemId.New(),
            householdId,
            shoppingListId,
            productId: null,
            freeText: freeText,
            unitId: unitId,
            note: note,
            createdAt: clock.UtcNow);

        // Free-text items always have exactly one Manual contribution.
        item._contributions.Add(ShoppingListItemContribution.Create(item.Id, ItemSource.Manual, null, quantity, unitId));
        return item;
    }

    /// <summary>
    /// Upserts a per-source contribution using (Source, SourceRef) as the match key (plantry-9scq).
    ///
    /// <para><b>New key</b> (no existing contribution matches): add a new contribution.</para>
    /// <para><b>Existing key</b>: top up that contribution by <paramref name="delta"/>
    ///   = max(0, incoming − that source's current quantity).
    ///   A delta of zero or negative is a no-op (need already covered).</para>
    ///
    /// <para>
    /// SourceRef idempotency rule (per design):
    /// Manual → SourceRef = null → one bucket per product row (re-add tops up).
    /// Recipe (ad-hoc) → SourceRef = recipeId → re-add same recipe is idempotent.
    /// MealPlan → SourceRef = entry/slot id (NOT recipeId) → same recipe on Mon+Thu = two distinct contributions that SUM.
    /// </para>
    ///
    /// Called exclusively by <see cref="ShoppingList.UpsertContribution"/>.
    /// </summary>
    /// <returns>True if a new contribution was added; false if an existing one was topped up.</returns>
    internal bool UpsertContribution(
        ItemSource source,
        Guid? sourceRef,
        decimal? delta,
        Guid? incomingUnitId,
        IClock clock)
    {
        var existing = FindContribution(source, sourceRef);
        if (existing is null)
        {
            // New (Source, SourceRef) pair — add a fresh contribution.
            var contribution = ShoppingListItemContribution.Create(Id, source, sourceRef, delta, incomingUnitId);
            _contributions.Add(contribution);

            // Adopt canonical unit if not yet set.
            if (incomingUnitId.HasValue && UnitId is null)
                UnitId = incomingUnitId;

            UpdatedAt = clock.UtcNow;
            return true;
        }

        // Existing key — top up only if there is a positive delta.
        if (delta.HasValue && delta.Value > 0m)
        {
            existing.TopUp(delta.Value, incomingUnitId);
            UpdatedAt = clock.UtcNow;
        }
        return false;
    }

    /// <summary>
    /// Idempotently SETS a source's contribution to an absolute quantity (plantry-gsj) — the
    /// SYNC verb, as opposed to the additive <see cref="UpsertContribution"/> top-up.
    ///
    /// <para>When no contribution matches (Source, SourceRef), a fresh one is created with
    /// <paramref name="quantity"/>. When one exists, its quantity is REPLACED (not incremented)
    /// with <paramref name="quantity"/>. Re-syncing the same target is therefore a no-op, and a
    /// servings change re-sets this source's slice rather than stacking onto it. Other sources'
    /// contributions on the same item are untouched, so cross-source sums (plantry-9scq) and the
    /// board's per-source attribution (plantry-26g) are preserved.</para>
    ///
    /// <para>Called exclusively by <see cref="ShoppingList.SetSourceContribution"/>.</para>
    /// </summary>
    /// <returns>Whether the slice was created, grown, left unchanged, or reduced.</returns>
    internal ContributionChange SetContribution(
        ItemSource source,
        Guid? sourceRef,
        decimal? quantity,
        Guid? incomingUnitId,
        IClock clock)
    {
        var existing = FindContribution(source, sourceRef);
        if (existing is null)
        {
            _contributions.Add(ShoppingListItemContribution.Create(Id, source, sourceRef, quantity, incomingUnitId));

            // Adopt canonical unit if not yet set (mirrors UpsertContribution).
            if (incomingUnitId.HasValue && UnitId is null)
                UnitId = incomingUnitId;

            UpdatedAt = clock.UtcNow;
            return ContributionChange.Created;
        }

        var before = existing.Quantity ?? 0m;
        var after = quantity ?? 0m;

        // Absolute set — the recipe slice becomes exactly this shortfall, in the item's canonical unit.
        existing.ReplaceQuantity(quantity, incomingUnitId ?? UnitId);
        if (incomingUnitId.HasValue && UnitId is null)
            UnitId = incomingUnitId;

        UpdatedAt = clock.UtcNow;
        return after > before ? ContributionChange.Increased
             : after < before ? ContributionChange.Reduced
             : ContributionChange.Unchanged;
    }

    /// <summary>
    /// Removes a source's contribution from this item, if present (plantry-gsj). Used by the
    /// whole-slice reconciliation: when a sync no longer targets a product, this source's slice on
    /// that row is dropped (a row left with zero contributions is then deleted by the caller).
    /// Other sources' contributions are untouched.
    /// </summary>
    /// <returns>True if a matching contribution was removed; false if none existed.</returns>
    internal bool RemoveContribution(ItemSource source, Guid? sourceRef, IClock clock)
    {
        var existing = FindContribution(source, sourceRef);
        if (existing is null)
            return false;

        _contributions.Remove(existing);
        UpdatedAt = clock.UtcNow;
        return true;
    }

    /// <summary>True when this item has at least one per-source contribution.</summary>
    public bool HasContributions => _contributions.Count > 0;

    /// <summary>
    /// Finds the contribution for a given (Source, SourceRef) pair.
    /// </summary>
    internal ShoppingListItemContribution? FindContribution(ItemSource source, Guid? sourceRef) =>
        _contributions.FirstOrDefault(c => c.Source == source && c.SourceRef == sourceRef);

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
    /// Sets the item's TOTAL quantity from the inline qty/unit editor (plantry-dem, plantry-5j2d).
    ///
    /// <para>The qty pill displays the item's derived TOTAL (<see cref="Quantity"/> — the sum of all
    /// contributions), so the editor submits a desired total, not a Manual-bucket value. The Manual
    /// contribution absorbs the difference above the non-Manual floor:
    ///   Manual := max(0, entered − sum(non-Manual contributions)).
    /// Recipe / MealPlan contributions are NEVER mutated — they set a floor the total cannot go below.
    /// A recipe-driven quantity is not user-editable, so clamping Manual at 0 (editing below the floor
    /// leaves the total at the non-Manual sum) is intended behaviour, not an edge case (plantry-5j2d).</para>
    ///
    /// <para>For a purely-Manual item the non-Manual floor is 0, so the entered value becomes the Manual
    /// quantity unchanged — submitting the pre-filled total is a true no-op. Quantity may be null
    /// (clears the Manual portion; the total then floors at the non-Manual sum). UnitId may be null
    /// (no unit). If no Manual contribution exists, one is created.</para>
    ///
    /// Called exclusively by <see cref="ShoppingList.EditItemQuantity"/>.
    /// </summary>
    internal void EditQuantity(decimal? quantity, Guid? unitId, IClock clock)
    {
        // Update the item's canonical unit (all contributions share this).
        UnitId = unitId;

        // Treat the entered value as the desired TOTAL; Manual absorbs whatever exceeds the
        // non-Manual floor. Clearing (null) drops the Manual portion entirely.
        decimal? manualTarget;
        if (quantity is null)
        {
            manualTarget = null;
        }
        else
        {
            var nonManualSum = _contributions
                .Where(c => c.Source != ItemSource.Manual)
                .Sum(c => c.Quantity ?? 0m);
            var absorbed = quantity.Value - nonManualSum;
            manualTarget = absorbed > 0m ? absorbed : 0m;
        }

        var manual = FindContribution(ItemSource.Manual, null);
        if (manual is null)
        {
            _contributions.Add(ShoppingListItemContribution.Create(Id, ItemSource.Manual, null, manualTarget, unitId));
        }
        else
        {
            manual.ReplaceQuantity(manualTarget, unitId);
        }

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
