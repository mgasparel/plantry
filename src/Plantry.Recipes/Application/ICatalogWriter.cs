namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption write port onto Catalog (recipes-domain-model.md §8). The two Catalog mutations the
/// recipe author performs inline: minting an untracked staple from a typed name (C12) and recording a
/// product-specific unit conversion the author supplies when no path exists (C10). Defined here in
/// Recipes.Application and <b>implemented in Plantry.Web</b> over Catalog's product commands. All
/// identifiers cross as raw <see cref="Guid"/> soft refs (DM-3).
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
    /// Adds a product-specific <c>ProductConversion</c> (C10) — the inline factor the author supplies
    /// when a unit→product conversion path is missing. Throws when Catalog rejects it (unknown product
    /// or unit, equal units).
    /// </summary>
    Task AddConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default);
}
