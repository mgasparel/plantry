namespace Plantry.Inventory.Application;

/// <summary>
/// The Catalog facts the Inventory application needs — product existence/guard for intake, and the
/// reference-data names the pantry read models render. Defined here and <b>implemented in
/// Plantry.Web</b> over Catalog's repositories, so the Inventory project keeps its
/// <c>→ SharedKernel only</c> dependency (the confirmed Port + Web-adapter seam). All identifiers
/// cross as raw <see cref="Guid"/> soft refs (inventory.md), consistent with the rest of the context.
/// </summary>
public interface ICatalogReadFacade
{
    /// <summary>Resolves a single product for the intake guard; null when it does not exist in this household.</summary>
    Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default);

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
    /// <summary>The resolved after-freezing due-days default (plantry-6owm rule 3) —
    /// <c>ExpiryDefaultResolver.ResolveDefaultDueDaysAfterFreezing</c>, already materialized here so
    /// <c>TransferStockCommand</c> can pass it straight to <c>ProductStock.Transfer</c> without
    /// Inventory reaching into Catalog. Null means no default is configured.</summary>
    int? DefaultDueDaysAfterFreezing = null,
    /// <summary>The resolved after-thawing due-days default (plantry-6owm rule 3), mirroring
    /// <see cref="DefaultDueDaysAfterFreezing"/>.</summary>
    int? DefaultDueDaysAfterThawing = null,
    /// <summary>True when the product is archived (plantry-lxm2) — only ever true on rows returned by
    /// <see cref="ICatalogReadFacade.ListArchivedProductsAsync"/>; every other source of
    /// <see cref="CatalogProductInfo"/> only ever supplies active products, so this defaults false.</summary>
    bool IsArchived = false);
