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
    /// Adds a batch of items to the household's shopping list with the specified provenance.
    /// <para>
    /// Each item carries a <see cref="ShoppingItem.ProductId"/>, <see cref="ShoppingItem.Quantity"/>
    /// (scaled to the desired serving count), and <see cref="ShoppingItem.UnitId"/>. The
    /// <paramref name="source"/> and <paramref name="sourceRef"/> are stamped uniformly on every
    /// row in the batch.
    /// </para>
    /// <para>Duplicate-handling (merge vs insert) is delegated to Shopping's add-item service. The
    /// Recipes caller does not control that policy; it simply passes all missing lines without
    /// pre-filtering for duplicates (DM-18).</para>
    /// </summary>
    /// <param name="items">The items to add. Empty collections are a no-op.</param>
    /// <param name="source">Provenance string — <c>"recipe"</c> for the J5 flow.</param>
    /// <param name="sourceRef">Soft ref to the originating entity; for J5 this is the <c>recipeId</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddItemsAsync(
        IEnumerable<ShoppingItem> items,
        string source,
        Guid sourceRef,
        CancellationToken ct = default);
}

/// <summary>
/// One product-backed item in a bulk add-to-shopping-list call (DM-18).
/// </summary>
/// <param name="ProductId">Soft ref → <c>catalog.product</c> (DM-3).</param>
/// <param name="Quantity">Required quantity, scaled to the desired serving count.</param>
/// <param name="UnitId">Soft ref → <c>catalog.unit</c> (DM-3).</param>
public sealed record ShoppingItem(Guid ProductId, decimal Quantity, Guid UnitId);
