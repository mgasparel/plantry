namespace Plantry.Inventory.Application;

/// <summary>
/// Anti-corruption write port onto Catalog for the Take Stock inline-add flow (P4-7, J5, TS-8).
/// Defined here in <c>Inventory.Application</c> and <b>implemented in <c>Plantry.Web</c></b> over
/// Catalog's <see cref="Plantry.Catalog.Application.CreateProductCommand"/> and
/// <see cref="Plantry.Catalog.Application.SetDefaultLocationCommand"/> — the exact analogue of
/// Recipes' <c>ICatalogWriter</c>/<c>CatalogWriterAdapter</c> (C12).
///
/// <para>The two Catalog mutations the Take Stock walk performs inline:</para>
/// <list type="bullet">
///   <item>Mint a new <b>tracked</b> product (<c>track_stock = true</c>) with a default location
///   set to the current walk location (C12, TS-8).</item>
///   <item>Set a product's default location without touching any other field (TS-9, J7).</item>
/// </list>
///
/// <para>All identifiers cross as raw <see cref="Guid"/> soft refs (DM-3). Throws on Catalog
/// rejection (e.g. duplicate name, unknown unit, unknown location) so the caller can surface the
/// error inline without an exception type boundary.</para>
/// </summary>
public interface ITakeStockCatalogWriter
{
    /// <summary>
    /// Inline-creates a tracked product (<c>track_stock = true</c>, C12) from the typed name, a
    /// default unit, an optional category, and the current walk location, returning the new product's id.
    ///
    /// <para>Throws when Catalog rejects the create (e.g. a duplicate name or unknown unit) —
    /// the caller searches first (J5 search-first dedupe), so create is the no-match path. The
    /// exception message carries the Catalog error code and description for inline surfacing.</para>
    /// </summary>
    Task<Guid> CreateTrackedProductAsync(
        string name,
        Guid defaultUnitId,
        Guid? categoryId,
        Guid defaultLocationId,
        CancellationToken ct = default);

    /// <summary>
    /// Inline-creates a tracked product as a variant of an existing group product
    /// (<c>CreateVariantCommand</c>), then records the opening-balance count (C8).
    ///
    /// <para>Inherits unit/category/location from the parent group unless overrides are supplied.
    /// Throws when Catalog rejects the create (e.g. unknown parent, max-depth violation, duplicate
    /// name, unknown unit) — the caller surfaces the error inline.</para>
    /// </summary>
    Task<Guid> CreateTrackedVariantAsync(
        Guid parentGroupId,
        string variantName,
        Guid? unitOverride,
        Guid? categoryOverride,
        Guid? locationOverride,
        CancellationToken ct = default);

    /// <summary>
    /// Inline-creates a new group (abstract parent, <c>trackStock = false</c>) and its first tracked
    /// variant (<c>CreateGroupedProductCommand</c>) atomically, then records the opening-balance count
    /// (C8). Returns the variant's product id (the stock-holding product).
    ///
    /// <para>Throws when Catalog rejects the create (e.g. duplicate group or variant name, unknown
    /// unit) — the caller surfaces the error inline.</para>
    /// </summary>
    Task<Guid> CreateTrackedGroupedProductAsync(
        string groupName,
        string variantName,
        Guid defaultUnitId,
        Guid? categoryId,
        Guid? defaultLocationId,
        CancellationToken ct = default);

    /// <summary>
    /// Sets a product's default storage location without touching any other field (TS-9). Used by J7
    /// ("No location" section) to file an unassigned product, and implicitly called when the inline-add
    /// flow picks an existing product that already has stock.
    ///
    /// <para>Throws when Catalog rejects the command (unknown product or location).</para>
    /// </summary>
    Task SetDefaultLocationAsync(
        Guid productId,
        Guid locationId,
        CancellationToken ct = default);
}
