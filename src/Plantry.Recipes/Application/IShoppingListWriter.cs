namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption write port onto Shopping (recipes-domain-model.md §8, DM-18). Allows the Recipes
/// context to add items to the household's shopping list without taking a direct dependency on the
/// Shopping bounded context. Defined here in Recipes.Application and <b>implemented in Plantry.Web</b>
/// over Shopping's add-item service, so the Recipes projects stay <c>→ SharedKernel only</c>.
///
/// <para>All traffic crosses as raw <see cref="Guid"/> soft refs (DM-3). The merge rule (shopping.md
/// resolved call 5) — incrementing quantity when an unchecked item for the same product already exists
/// — is Shopping's concern: the adapter passes <c>intentionalDuplicate = false</c> to the underlying
/// <see cref="Plantry.Shopping.Application.AddItemCommand"/> so the merge happens automatically.</para>
/// </summary>
public interface IShoppingListWriter
{
    /// <summary>
    /// Idempotently SYNCS this source's contribution slice on the household's shopping list to
    /// exactly <paramref name="items"/> (plantry-gsj). Unlike an additive add, re-syncing the same
    /// target set is a no-op (no quantity drift): the source's own slice is SET to each item's
    /// quantity, products no longer targeted have this source's slice removed, and other sources'
    /// slices on the same rows are preserved (plantry-9scq sums / plantry-26g attribution).
    /// <para>
    /// Each item carries a <see cref="ShoppingItem.ProductId"/>, <see cref="ShoppingItem.Quantity"/>
    /// (scaled to the desired serving count), and <see cref="ShoppingItem.UnitId"/>. The
    /// <paramref name="source"/> and <paramref name="sourceRef"/> identify the slice to reconcile.
    /// </para>
    /// <para>Merge/idempotency/no-resurrection policy is Shopping's concern (ADR-002, DM-18); the
    /// Recipes caller passes the full target set and reads back the outcome counts.</para>
    /// </summary>
    /// <param name="items">The full target set for this slice. An empty set reconciles the slice away.</param>
    /// <param name="source">Provenance string — <c>"recipe"</c> for the recipe flows.</param>
    /// <param name="sourceRef">Soft ref identifying the slice; for recipes this is the <c>recipeId</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-target counts (added / already-present / checked-off) for the result summary.</returns>
    Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
        IReadOnlyList<ShoppingItem> items,
        string source,
        Guid sourceRef,
        CancellationToken ct = default);
}

/// <summary>
/// Per-target outcome counts returned by <see cref="IShoppingListWriter.SyncSourceContributionAsync"/>
/// (plantry-gsj) — surfaced to the user as "Added X · Y already on your list · Z checked off". A neutral
/// Recipes-side DTO so the port stays free of any Shopping type dependency (SharedKernel-only).
/// </summary>
/// <param name="Added">Targets whose slice was created or grown.</param>
/// <param name="AlreadyPresent">Targets whose slice already covered the shortfall (no change).</param>
/// <param name="CheckedOff">Targets skipped because only a checked-off row exists (no resurrection).</param>
public sealed record ShoppingSyncOutcome(int Added, int AlreadyPresent, int CheckedOff)
{
    /// <summary>A zero outcome — nothing added, already present, or skipped (e.g. a no-op press).</summary>
    public static readonly ShoppingSyncOutcome None = new(0, 0, 0);

    /// <summary>True when nothing at all was reconciled — used to suppress an empty summary line.</summary>
    public bool IsEmpty => Added == 0 && AlreadyPresent == 0 && CheckedOff == 0;
}

/// <summary>
/// One product-backed item in a bulk add-to-shopping-list call (DM-18).
/// </summary>
/// <param name="ProductId">Soft ref → <c>catalog.product</c> (DM-3).</param>
/// <param name="Quantity">Required quantity, scaled to the desired serving count.</param>
/// <param name="UnitId">Soft ref → <c>catalog.unit</c> (DM-3).</param>
public sealed record ShoppingItem(Guid ProductId, decimal Quantity, Guid UnitId);
