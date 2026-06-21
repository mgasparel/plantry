namespace Plantry.Inventory.Application;

// ─── Read models ──────────────────────────────────────────────────────────────

/// <summary>
/// A location row as shown on the Take Stock walk index (one entry per active Catalog location).
/// </summary>
public sealed record TakeStockLocationRow(
    Guid LocationId,
    string LocationName);

/// <summary>
/// One product row on the "walk a location" card (C5 union branch A or B).
/// Branch A: the product has active stock in this location; <see cref="RecordedQuantity"/> is the
/// sum in the product's display unit and <see cref="HasActiveStock"/> is true.
/// Branch B: the product's Catalog default_location_id matches this location but there is no
/// active stock here; <see cref="RecordedQuantity"/> is 0 and <see cref="HasActiveStock"/> is false.
/// </summary>
public sealed record TakeStockLocationProductRow(
    Guid ProductId,
    string ProductName,
    string DisplayUnitCode,
    /// <summary>Sum of active lots in the product's display unit (0 if no active stock in this location).</summary>
    decimal RecordedQuantity,
    /// <summary>True when branch A (active stock present); false when branch B (default-location only).</summary>
    bool HasActiveStock,
    /// <summary>
    /// The Guid of the product's display unit — used by the walk page to pass the unit id to
    /// <see cref="SaveCountsCommand"/> (P4-4b). Defaults to <see cref="Guid.Empty"/> when the unit
    /// cannot be resolved (guards against forward-compatibility breakage).
    /// </summary>
    Guid DisplayUnitId = default);

/// <summary>
/// One product row on the "No location" section (J7): tracked products that have active stock
/// whose Catalog <c>default_location_id</c> is null (no home location assigned).
/// </summary>
public sealed record TakeStockNoLocationRow(
    Guid ProductId,
    string ProductName,
    string DisplayUnitCode,
    decimal RecordedQuantity);

/// <summary>
/// One active lot for a product in a location — shown in the lot escape-hatch view (ListLots).
/// <see cref="UnitId"/> is the Catalog unit Guid needed by the server when the UI sends per-lot
/// adjustments back (P4-5 / <see cref="LotAdjustItem"/>).
/// </summary>
public sealed record TakeStockLotRow(
    Guid EntryId,
    decimal Quantity,
    string UnitCode,
    Guid UnitId,
    DateOnly? ExpiryDate,
    bool IsOpen);

/// <summary>
/// One search result for the inline product search during a Take Stock walk (SearchProducts).
/// </summary>
public sealed record TakeStockProductMatch(
    Guid ProductId,
    string Name,
    string DefaultUnitCode,
    Guid DefaultLocationId);

// ─── Port ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Read port for the Take Stock flow (P4-3). Defined in <c>Inventory.Application</c>; implemented
/// in <c>Plantry.Web</c> as an adapter composing the Inventory <see cref="IProductStockRepository"/>
/// and Catalog repositories — keeping both Inventory projects free of any Catalog dependency.
///
/// All five methods are household-scoped via the ambient <see cref="SharedKernel.Tenancy.ITenantContext"/>;
/// implementations must enforce both the EF query filter and the RLS backstop.
/// </summary>
public interface ITakeStockReader
{
    /// <summary>
    /// Returns the active Catalog locations for the current household, ordered by name.
    /// Used to build the location walk index page.
    /// </summary>
    Task<IReadOnlyList<TakeStockLocationRow>> ListLocationsAsync(CancellationToken ct = default);

    /// <summary>
    /// C5 union: returns the product rows for one location's count card.
    /// Branch A — tracked products with active stock in <paramref name="locationId"/> (recorded sum
    /// in the display unit, <see cref="TakeStockLocationProductRow.HasActiveStock"/> = true).
    /// Branch B — tracked products whose Catalog <c>default_location_id</c> equals
    /// <paramref name="locationId"/> but whose active stock in this location is zero
    /// (<see cref="TakeStockLocationProductRow.HasActiveStock"/> = false, quantity = 0).
    /// The union is deduplicated: a product that satisfies both branches appears once (branch A wins).
    /// Results are ordered by product name.
    /// </summary>
    Task<IReadOnlyList<TakeStockLocationProductRow>> ListLocationRowsAsync(
        Guid locationId, CancellationToken ct = default);

    /// <summary>
    /// J7: tracked products with active stock whose Catalog <c>default_location_id</c> is null
    /// (no home location). The recorded quantity is the per-product sum across ALL locations,
    /// expressed in the product's display unit.
    /// Results are ordered by product name.
    /// </summary>
    Task<IReadOnlyList<TakeStockNoLocationRow>> ListNoLocationRowsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the active lots for a specific (product, location) pair. Used by the lot
    /// escape-hatch view so the user can correct individual lots rather than the aggregate total.
    /// </summary>
    Task<IReadOnlyList<TakeStockLotRow>> ListLotsAsync(
        Guid productId, Guid locationId, CancellationToken ct = default);

    /// <summary>
    /// Inline product search for the Take Stock walk (TS-10 / SearchProducts).
    /// Matches products by exact or contains-match on name (case-insensitive).
    /// Fuzzy matching is deferred to plantry-hl4a.
    /// Only tracked (<c>CanHoldStock = true</c>) and non-archived products are returned.
    /// Results are ordered: exact matches first, then contains, then alphabetically.
    /// </summary>
    Task<IReadOnlyList<TakeStockProductMatch>> SearchProductsAsync(
        string query, CancellationToken ct = default);
}
