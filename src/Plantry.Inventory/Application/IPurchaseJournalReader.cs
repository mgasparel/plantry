namespace Plantry.Inventory.Application;

/// <summary>
/// A focused read over Inventory's <b>purchase journal</b> — the <c>AddStock</c> /
/// <see cref="Plantry.Inventory.Domain.StockReason.Purchase"/> movements that are the lean, truest record of
/// "what the household actually buys" (DL-O4). Returns per-product purchase counts within a window, grouped
/// in the database so only the counts cross the wire, never the raw journal rows.
///
/// <para>Kept as a dedicated port (rather than another method on <c>IProductStockRepository</c> or the
/// pantry read facade) because it answers a different question — cross-product purchase frequency — and has
/// exactly one consumer: the Deals stock-up alerts (P5-10). The <b>threshold and window</b> that define
/// "frequently bought" are a Deals policy (<c>StockUpAlerts</c>), not this reader's concern — it is a pure
/// count read. Household scoping is enforced by the <c>InventoryDbContext</c> RLS query filter, so no
/// household argument is carried.</para>
/// </summary>
public interface IPurchaseJournalReader
{
    /// <summary>
    /// Per-product count of purchase movements occurring at or after <paramref name="since"/>, scoped to the
    /// signed-in household. Products with no purchases in the window are absent from the map.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> CountPurchasesSinceAsync(
        DateTimeOffset since, CancellationToken ct = default);
}
