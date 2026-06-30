using Plantry.SharedKernel.Domain;

namespace Plantry.Shopping.Domain;

/// <summary>
/// Per-source quantity contribution for a <see cref="ShoppingListItem"/>.
///
/// <para>
/// Match key for upsert is (Source, SourceRef) — see <see cref="ShoppingListItem.UpsertContribution"/>.
/// A distinct (Source, SourceRef) pair yields a new contribution; an existing pair tops up.
/// The item's total <see cref="ShoppingListItem.Quantity"/> is the SUM of all contributions.
/// </para>
///
/// <para>
/// SourceRef semantics (per design):
/// <list type="bullet">
///   <item><description>Manual: SourceRef = null — one manual bucket per product row.</description></item>
///   <item><description>Recipe (ad-hoc): SourceRef = recipeId — re-add is idempotent.</description></item>
///   <item><description>MealPlan (future): SourceRef = entry/slot id, NOT recipeId — same recipe twice = two contributions.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class ShoppingListItemContribution : Entity<ShoppingListItemContributionId>
{
    // EF constructor
    private ShoppingListItemContribution() { }

    private ShoppingListItemContribution(
        ShoppingListItemContributionId id,
        ShoppingListItemId itemId,
        ItemSource source,
        Guid? sourceRef,
        decimal? quantity,
        Guid? unitId)
    {
        Id = id;
        ItemId = itemId;
        Source = source;
        SourceRef = sourceRef;
        Quantity = quantity;
        UnitId = unitId;
    }

    /// <summary>FK to the parent <see cref="ShoppingListItem"/>.</summary>
    public ShoppingListItemId ItemId { get; private set; }

    /// <summary>Provenance type — Manual / Recipe / MealPlan / Deal.</summary>
    public ItemSource Source { get; private set; }

    /// <summary>
    /// Opaque demand identity. For Manual: null. For Recipe: recipeId.
    /// For MealPlan (future): slot/entry id (NOT recipeId — two Mon+Thu slots must sum).
    /// Equality is the only operation ever performed on this value; no name resolution happens here.
    /// </summary>
    public Guid? SourceRef { get; private set; }

    /// <summary>
    /// Quantity contributed by this source, in the parent item's canonical unit.
    /// Null when no quantity was specified.
    /// </summary>
    public decimal? Quantity { get; private set; }

    /// <summary>
    /// The unit for this contribution's Quantity. All contributions on one item share the
    /// same unit (unit-incompatible adds split to a separate row at the item level).
    /// </summary>
    public Guid? UnitId { get; private set; }

    /// <summary>
    /// Creates a new contribution for a given source/sourceRef pair.
    /// </summary>
    internal static ShoppingListItemContribution Create(
        ShoppingListItemId itemId,
        ItemSource source,
        Guid? sourceRef,
        decimal? quantity,
        Guid? unitId)
    {
        return new ShoppingListItemContribution(
            ShoppingListItemContributionId.New(),
            itemId,
            source,
            sourceRef,
            quantity,
            unitId);
    }

    /// <summary>
    /// Tops up this contribution's quantity by <paramref name="delta"/>.
    /// When delta is null: quantity unchanged.
    /// When current quantity is null: set to delta.
    /// </summary>
    internal void TopUp(decimal? delta, Guid? incomingUnitId)
    {
        if (delta.HasValue)
        {
            Quantity = Quantity.HasValue ? Quantity.Value + delta.Value : delta.Value;
        }

        // Adopt unitId if we don't have one (mirrors previous MergeFrom behaviour).
        if (incomingUnitId.HasValue && UnitId is null)
            UnitId = incomingUnitId;
    }

    /// <summary>
    /// Replaces (not adds to) this contribution's quantity and unit.
    /// Used by the direct inline editor — the user is setting an absolute value, not topping up.
    /// </summary>
    internal void ReplaceQuantity(decimal? quantity, Guid? unitId)
    {
        Quantity = quantity;
        UnitId = unitId;
    }
}
