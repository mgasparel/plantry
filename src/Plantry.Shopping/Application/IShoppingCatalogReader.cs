namespace Plantry.Shopping.Application;

/// <summary>
/// Anti-corruption port: Shopping's read model needs product name, category name, and unit code
/// from the Catalog context. This interface is defined in Shopping.Application and implemented in
/// the Web layer (an adapter over <c>IProductRepository</c> / <c>ICategoryRepository</c> /
/// <c>IUnitRepository</c>), following the same ACL pattern as
/// <c>Plantry.Recipes.Application.ICatalogProductReader</c>.
/// </summary>
public interface IShoppingCatalogReader
{
    /// <summary>
    /// Resolves the product name and category name for a set of product ids in one call.
    /// Missing product ids (not found in catalog) are silently omitted.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default);

    /// <summary>Resolves unit codes by unit id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all active products ordered by name — used to populate the product search
    /// dropdown on the add-item form (via the searchable-select handler).
    /// </summary>
    Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Attempts to convert <paramref name="amount"/> from <paramref name="fromUnitId"/> into
    /// <paramref name="toUnitId"/> using the household's unit table and
    /// <paramref name="productId"/>'s product-specific conversion overrides.
    /// Returns the converted quantity on success, <c>null</c> when no conversion path exists
    /// (i.e. the units are cross-dimension and no product conversion bridges them).
    /// Delegates to <c>Catalog.Domain.UnitConverter</c> via the adapter — Shopping never reads
    /// Catalog's EF context directly (ADR-002).
    /// </summary>
    Task<decimal?> TryConvertAsync(
        decimal amount,
        Guid fromUnitId,
        Guid toUnitId,
        Guid productId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all active units for the household, ordered by code — used to populate the unit
    /// select on the add-item form and inline qty editor (plantry-259).
    /// Units are catalog/household-defined; the list is sourced from <c>IUnitRepository</c>.
    /// </summary>
    Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all active (non-archived) categories for the household, ordered by name —
    /// used to populate the recategorize dropdown for uncategorized items (plantry-259).
    /// Categories are catalog/household-defined; the list is sourced from <c>ICategoryRepository</c>.
    /// </summary>
    Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default);
}

/// <summary>
/// Lightweight unit option for the add-item unit select and inline qty editor.
/// <see cref="Dimension"/> is the raw <c>Catalog.Domain.Dimension</c> db value ("mass"/"volume"/"count")
/// so the add-item &lt;select&gt; can group by dimension (plantry-n9iw) without Shopping.Application
/// taking a dependency on Catalog.Domain's enum type.
/// </summary>
public sealed record ShoppingUnitOption(Guid UnitId, string Code, string Name, string Dimension);

/// <summary>Lightweight category option for the recategorize dropdown.</summary>
public sealed record ShoppingCategoryOption(Guid CategoryId, string Name, int? Hue);

/// <summary>Summary of a catalog product for Shopping read-model enrichment.</summary>
public sealed record ShoppingProductSummary(
    Guid ProductId,
    string Name,
    string? CategoryName,
    /// <summary>Hue in degrees (0–359) on the oklch colour wheel, inherited from the product's category. Null when uncategorised or category has no hue.</summary>
    int? CategoryHue = null);

/// <summary>Lightweight product option for the add-item product search.</summary>
public sealed record ShoppingProductCandidate(Guid ProductId, string Name);
