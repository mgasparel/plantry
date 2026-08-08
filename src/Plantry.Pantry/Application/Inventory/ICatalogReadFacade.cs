using Plantry.SharedKernel.Domain;

namespace Plantry.Pantry.Application;

/// <summary>
/// The Catalog facts the Inventory-side application code needs — product existence/guard for intake,
/// and the reference-data names the pantry read models render. Its adapter composes Catalog's
/// repositories directly — an intra-context Pantry collaboration now that Catalog and Inventory live
/// in one assembly (ADR-024, plantry-g3da.6). All identifiers cross as raw <see cref="Guid"/> soft
/// refs (inventory.md), consistent with the rest of the context.
/// </summary>
public interface ICatalogReadFacade
{
    /// <summary>Resolves a single product for the intake guard; null when it does not exist in this household.</summary>
    Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Batch counterpart to <see cref="FindProductAsync"/> (plantry-hbol): resolves every given product id
    /// in a small constant number of round trips instead of one per product — for callers (e.g.
    /// <c>InventoryStockReaderAdapter.FindStockBatchAsync</c>) that need catalog facts for a whole set of
    /// stocked products at once. Ids not found in this household are simply absent from the result.
    /// Defaults to a per-id <see cref="FindProductAsync"/> loop so existing test doubles need not
    /// implement it; the real Web adapter overrides it with a batched query.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, CatalogProductInfo>> FindManyAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, CatalogProductInfo>();
        foreach (var productId in productIds.Distinct())
        {
            var info = await FindProductAsync(productId, ct);
            if (info is not null)
                result[productId] = info;
        }
        return result;
    }

    /// <summary>All active products, for joining names onto the pantry list.</summary>
    Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default);

    /// <summary>
    /// Archived products — the counterpart to <see cref="ListProductsAsync"/> for
    /// <see cref="InventoryQueryService.ListPantryAsync"/> and <see cref="InventoryQueryService.CountInStockAsync"/>
    /// (plantry-lxm2): a product's stock persists after archival, so these two read models need
    /// archived products' names/units too, or they would silently skip a household's on-hand
    /// archived-but-still-stocked lots. Other read models (expiring-soon, take-stock) intentionally
    /// keep the active-only <see cref="ListProductsAsync"/> — this port is scoped narrowly to the
    /// two callers that need it. Defaults to an empty list so existing test doubles need not
    /// implement it (mirrors <see cref="GetLocationFrozenFlagsAsync"/>); only the real Web adapter
    /// and any archival-focused test double need to override it.
    /// </summary>
    Task<IReadOnlyList<CatalogProductInfo>> ListArchivedProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>([]);

    /// <summary>Unit code by unit id (e.g. "g", "ml") for rendering lot quantities.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default);

    /// <summary>Location name by location id for the pantry/detail views.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Location frozen-ness (<c>LocationType.Frozen</c>) by location id (plantry-6owm) — lets
    /// <c>TransferStockCommand</c> derive the implicit freeze/thaw transition kind (rule 2) without
    /// Inventory reaching into Catalog. Locations absent from the household are simply absent from the
    /// result. Defaults to an empty dictionary so existing test doubles need not implement it (mirrors
    /// <see cref="IProductStockRepository.ListProductIdsWithStockAsync"/>'s default-implementation
    /// pattern) — only the real Web adapter and any Move/Transfer-focused test double need to override it.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, bool>> GetLocationFrozenFlagsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, bool>>(new Dictionary<Guid, bool>());
}

/// <summary>The slice of a Catalog product the Inventory read models and intake guard depend on.</summary>
public sealed record CatalogProductInfo(
    Guid Id,
    string Name,
    string? CategoryName,
    Guid DefaultUnitId,
    string DefaultUnitCode,
    bool CanHoldStock,
    bool IsVariant = false,
    /// <summary>Hue in degrees (0–359) on the oklch colour wheel, inherited from the product's category. Null when uncategorised or category has no hue.</summary>
    int? CategoryHue = null,
    /// <summary>
    /// The resolved after-opening due-days default (DM-11 rule 1, plantry-1le6) — Catalog's
    /// <c>ExpiryDefaultResolver.ResolveDefaultDueDaysAfterOpening</c> fallback chain, already
    /// materialized here so <c>MarkStockOpenedCommand</c>/<c>ConsumeStockCommand</c> can pass it
    /// straight to <c>ProductStock.MarkOpened</c>/<c>Consume</c> without Inventory reaching into
    /// Catalog. Null means no default is configured.
    /// </summary>
    int? DefaultDueDaysAfterOpening = null,
    /// <summary>True when the product is archived (plantry-lxm2) — only ever true on rows returned by
    /// <see cref="ICatalogReadFacade.ListArchivedProductsAsync"/>; every other source of
    /// <see cref="CatalogProductInfo"/> only ever supplies active products, so this defaults false.</summary>
    bool IsArchived = false,
    /// <summary>
    /// The normal resolved policy for a future freeze. The Web Catalog adapter always supplies
    /// <see cref="ExpiryTransitionPolicy.Never"/> or <see cref="ExpiryTransitionPolicy.Days"/> for
    /// an existing product. Null is retained only for older/test port doubles; the missing-product
    /// fallback is selected by <see cref="TransferStockCommand"/>, not by a resolver outcome.
    /// </summary>
    ExpiryTransitionPolicy? AfterFreezingPolicy = null,
    /// <summary>The normal resolved policy for a future thaw; see <see cref="AfterFreezingPolicy"/>.</summary>
    ExpiryTransitionPolicy? AfterThawingPolicy = null,
    /// <summary>
    /// True when the product was produced at home (a recipe yield or cook leftover, "made, not
    /// bought", plantry-sn6v) rather than bought — mirrors <c>Product.IsProduced</c>. Drives the
    /// restock-candidate exclusion in <c>ShoppingPantryReaderAdapter.GetLowStockProductsAsync</c>.
    /// </summary>
    bool IsProduced = false,
    /// <summary>
    /// The product's configured typical storage location (<c>Product.DefaultLocationId</c>, plantry-iypo)
    /// — the same field Take Stock and product intake already use as "where this normally lives". Lets
    /// <c>InventoryProducerAdapter.ProduceAsync</c> store a cooked yield in the product's usual location
    /// instead of an arbitrary alphabetically-first active location. Null when the product has no
    /// configured default (e.g. a freshly auto-created yield product).
    /// </summary>
    Guid? DefaultLocationId = null);
