namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption write port onto Catalog (recipes-domain-model.md §8). The Catalog mutations the
/// recipe author performs inline: minting an untracked staple from a typed name (C12), creating a
/// tracked product (optionally group-aware, plantry-orix), and recording a product-specific unit
/// conversion the author supplies when no path exists (C10). Defined here in Recipes.Application and
/// <b>implemented in Plantry.Web</b> over Catalog's product commands. All identifiers cross as raw
/// <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public interface ICatalogWriter
{
    /// <summary>
    /// Inline-creates an untracked staple (<c>track_stock = false</c>, C12) from the typed name and a
    /// default unit, returning the new product's id. Throws when Catalog rejects the create (e.g. a
    /// duplicate name or unknown unit) — the author flow searches first, so create is the no-match path.
    /// </summary>
    Task<Guid> CreateUntrackedStapleAsync(string name, Guid defaultUnitId, CancellationToken ct = default);

    /// <summary>
    /// Inline-creates a standalone tracked product (<c>track_stock = true</c>) from the typed name and a
    /// default unit, returning the new product's id. Unlike <see cref="CreateUntrackedStapleAsync"/>,
    /// the product participates in stock tracking. Category is optional. Recipes have no location
    /// concept so no <c>defaultLocationId</c> is passed. Throws when Catalog rejects the create.
    /// </summary>
    Task<Guid> CreateTrackedProductAsync(string name, Guid defaultUnitId, Guid? categoryId, CancellationToken ct = default);

    /// <summary>
    /// Inline-creates a tracked product as a variant of an existing group product
    /// (<see cref="Plantry.Catalog.Application.CreateVariantCommand"/>). Inherits unit/category from
    /// the parent group unless overrides are supplied. Throws when Catalog rejects the create (e.g.
    /// unknown parent, max-depth violation, duplicate name).
    /// </summary>
    Task<Guid> CreateTrackedVariantAsync(Guid parentGroupId, string variantName, Guid? unitOverride, Guid? categoryOverride, CancellationToken ct = default);

    /// <summary>
    /// Inline-creates a new group (abstract parent, <c>trackStock = false</c>) and its first tracked
    /// variant (<see cref="Plantry.Catalog.Application.CreateGroupedProductCommand"/>) atomically.
    /// Returns the variant's product id (the stock-holding product). Throws when Catalog rejects the
    /// create (e.g. duplicate group or variant name, unknown unit).
    /// </summary>
    Task<Guid> CreateTrackedGroupedProductAsync(string groupName, string variantName, Guid defaultUnitId, Guid? categoryId, CancellationToken ct = default);

    /// <summary>
    /// Adds a product-specific <c>ProductConversion</c> (C10) — the inline factor the author supplies
    /// when a unit→product conversion path is missing. Throws when Catalog rejects it (unknown product
    /// or unit, equal units).
    /// </summary>
    Task AddConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default);
}
