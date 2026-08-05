namespace Plantry.Recipes.Application;

/// <summary>
/// Read-side seam for the <see cref="Plantry.Recipes.Domain.Substitution"/> edge (plantry-aqpa.1), for
/// consumers other than the create/delete commands themselves — mirrors the
/// <see cref="ICatalogProductReader"/> reader-interface seam (a dedicated read port alongside the
/// write-side repository), though this one stays Recipes-internal (both the entity and its readers live
/// in Recipes, so no <c>Plantry.Composition</c> cross-context adapter is needed; a later fulfillment/cook/
/// shopping child bead may promote this into one if it needs to read edges from outside Recipes).
/// </summary>
public interface ISubstitutionReader
{
    /// <summary>
    /// Batch "get edges whose TARGET is any of these product ids" — the fulfillment/cook/shopping
    /// direction: given a recipe's ingredient product ids, find every substitution that could satisfy
    /// each one, in a single round-trip. Product ids with no substitution edges are simply absent from
    /// the result (never an empty-list entry).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>>> ListByTargetProductIdsAsync(
        IReadOnlyList<Guid> targetProductIds, CancellationToken ct = default);

    /// <summary>
    /// "Get all edges touching product X" — the product-detail UI direction: every edge where the given
    /// product appears as either the target or the substitute, so a product's detail page can show both
    /// "this can be substituted by…" and "this can substitute for…".
    /// </summary>
    Task<IReadOnlyList<SubstitutionEdge>> ListTouchingProductAsync(
        Guid productId, CancellationToken ct = default);
}

/// <summary>
/// Read-model projection of a <see cref="Plantry.Recipes.Domain.Substitution"/> edge — the display slice
/// consumers of <see cref="ISubstitutionReader"/> need, decoupled from the domain aggregate the way
/// <c>CatalogProduct</c> decouples <see cref="ICatalogProductReader"/> from Catalog's own entities.
/// </summary>
public sealed record SubstitutionEdge(
    Guid Id,
    Guid TargetProductId,
    decimal TargetQuantity,
    Guid TargetUnitId,
    Guid SubstituteProductId,
    decimal SubstituteQuantity,
    Guid SubstituteUnitId);
