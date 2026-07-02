namespace Plantry.Deals.Application;

/// <summary>
/// Anti-corruption read port onto <b>Inventory</b>'s purchase journal (DL-O4). Returns, per product, the
/// number of <b>purchase</b> movements (<c>AddStock</c> / <c>StockReason.Purchase</c> rows — the lean,
/// truest "what we actually buy") recorded at or after <paramref name="since"/>. The window boundary is a
/// <see cref="StockUpAlerts"/> policy passed in as an absolute instant, so this port stays a pure data read
/// with no clock or threshold knowledge of its own.
///
/// <para>Owned by <c>Plantry.Deals.Application</c> and implemented in <c>Plantry.Web</c> over Inventory's
/// read facade, keeping <c>Plantry.Deals</c> free of any Inventory dependency (ADR-010/DM-3) — the same
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
}
