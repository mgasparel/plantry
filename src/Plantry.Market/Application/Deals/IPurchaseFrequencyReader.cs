namespace Plantry.Market.Application;

/// <summary>
/// Anti-corruption read port onto <b>Inventory</b>'s purchase journal (DL-O4). Returns, per product, the
/// number of <b>purchase</b> movements (<c>AddStock</c> / <c>StockReason.Purchase</c> rows — the lean,
/// truest "what we actually buy") recorded at or after <paramref name="since"/>. The window boundary is a
/// <see cref="StockUpAlerts"/> policy passed in as an absolute instant, so this port stays a pure data read
/// with no clock or threshold knowledge of its own.
///
/// <para>Owned by <c>Plantry.Market.Application</c> and implemented in <c>Plantry.Web</c> over Inventory's
/// read facade, keeping <c>Plantry.Market</c> free of any Inventory dependency (ADR-010/DM-3) — the same
/// per-context port shape as <c>ICatalogProductReader</c> here and <c>IMealPlanShoppingWriter</c> in
/// MealPlanning. Household scoping is enforced at the Inventory RLS layer, so no household argument is
/// carried across the boundary.</para>
/// </summary>
public interface IPurchaseFrequencyReader
{
    /// <summary>
    /// Purchase-movement counts per product since <paramref name="since"/> (inclusive), household-scoped.
    /// Products with no purchases in the window are absent from the map (not present with a zero count).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> PurchaseCountsSinceAsync(
        DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// Every purchase-movement timestamp for each of the given products (household-scoped), oldest-first
    /// per product — unlike <see cref="PurchaseCountsSinceAsync"/>, which only counts, this returns the
    /// individual dates a "buy this every ~3 weeks" cadence estimate needs (plantry-gtgl, Deals review
    /// purchase context). No window: the full purchase-journal history for the product is used, since a
    /// cadence estimate wants as many data points as exist, not a fixed trailing slice. Products with no
    /// purchases are absent from the map.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DateTimeOffset>>> PurchaseDatesForProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default);
}
