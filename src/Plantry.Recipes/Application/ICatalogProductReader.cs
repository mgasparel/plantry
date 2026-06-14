namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption read port onto Catalog (recipes-domain-model.md §8). Gives Author, Cook, and
/// Fulfillment the slice of a product they need — name, <c>track_stock</c>, default unit, and the
/// depth-1 parent/variant tree (DM-10, DM-19) — plus a name search for the ingredient editor. Defined
/// here in Recipes.Application and <b>implemented in Plantry.Web</b> over Catalog's repositories, so the
/// Recipes project keeps its <c>→ SharedKernel only</c> dependency. All identifiers cross as raw
/// <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public interface ICatalogProductReader
{
    /// <summary>Resolves a single product (with its parent/variant tree); null when it does not exist in this household.</summary>
    Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Name search for the ingredient editor — active products whose name contains
    /// <paramref name="nameQuery"/> (case-insensitive). Empty/whitespace query returns no candidates.
    /// </summary>
    Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default);
}

/// <summary>
/// The slice of a Catalog product Recipes depends on, including the depth-1 parent/variant tree.
/// <see cref="VariantProductIds"/> holds the live (non-archived) variants when this product is a
/// parent (DM-19 rollup); <see cref="ParentProductId"/> is set when this product is itself a variant.
/// </summary>
public sealed record CatalogProduct(
    Guid Id,
    string Name,
    bool TrackStock,
    Guid DefaultUnitId,
    Guid? ParentProductId,
    bool IsParent,
    IReadOnlyList<Guid> VariantProductIds);

/// <summary>A search hit for the ingredient editor's product picker.</summary>
public sealed record CatalogProductCandidate(
    Guid Id,
    string Name,
    bool TrackStock,
    Guid DefaultUnitId);
