namespace Plantry.Pantry.Application;

/// <summary>
/// A focused read over Inventory's stock journal, scoped to <see cref="Plantry.Pantry.Domain.StockReason.Discarded"/>
/// rows (plantry-h9z9, Today "did you know" injection point) — mirrors <see cref="IPurchaseJournalReader"/>'s
/// shape (a dedicated port over one <c>Reason</c> slice of the journal, aggregated in the database so only
/// scalars cross the wire) but for the "wasted, not used" side of the ledger rather than purchases.
///
/// <para>Kept as its own port (rather than another method on <c>IProductStockRepository</c> or
/// <c>InventoryQueryService</c>) for the same reason <see cref="IPurchaseJournalReader"/> is: it answers a
/// household-wide question — not a per-product one like <see cref="InventoryQueryService.GetConsumptionStatsAsync"/>'s
/// <c>WasteRate</c> — and has exactly one consumer (the Today stats widget). Household scoping is enforced by
/// the <c>PantryDbContext</c> RLS query filter, so no household argument is carried.</para>
/// </summary>
public interface IWasteJournalReader
{
    /// <summary>
    /// Count of Discarded movements occurring at or after <paramref name="since"/>, scoped to the
    /// signed-in household — a whole-household "wasted items" count, deliberately event-count rather than
    /// quantity-weighted (a mixed-unit sum across products would need a per-row unit conversion this reader
    /// has no product-conversion context for; a plain count needs none and is still a meaningful "did you
    /// know" signal).
    /// </summary>
    Task<int> CountDiscardedSinceAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// The most recent Discarded movement's timestamp across the whole household, or null when nothing has
    /// ever been discarded — the "days since anything expired" streak chip's raw input.
    /// </summary>
    Task<DateTimeOffset?> MostRecentDiscardAsync(CancellationToken ct = default);
}
