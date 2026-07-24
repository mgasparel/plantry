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

    /// <summary>Unit code by unit id (e.g. "g", "ml") for rendering lot quantities.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default);

    /// <summary>Location name by location id for the pantry/detail views.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default);
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
    int? DefaultDueDaysAfterOpening = null);
