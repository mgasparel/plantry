using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;

namespace Plantry.Inventory.Infrastructure;

/// <summary>
/// EF-backed <see cref="IPurchaseJournalReader"/> (P5-10 / DL-O4). Groups the <c>stock_journal_entry</c>
/// purchase rows in the database and materializes only per-product counts. The
/// <c>(household_id, product_id)</c> index on the journal covers the grouping; the <c>Reason</c>
/// value-converter comparison translates to a <c>WHERE reason = 'Purchase'</c> at the SQL layer.
///
/// <para>Household scoping is handled by <see cref="InventoryDbContext"/>'s RLS query filter (armed per
/// request by <c>RlsMiddleware</c>), the same way the pantry/detail read models are scoped — so this reader
/// carries no household argument and never sees another household's journal.</para>
/// </summary>
public sealed class PurchaseJournalReader(InventoryDbContext db) : IPurchaseJournalReader
{
    public async Task<IReadOnlyDictionary<Guid, int>> CountPurchasesSinceAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        var counts = await db.StockJournalEntries
            .Where(j => j.Reason == StockReason.Purchase && j.OccurredAt >= since)
            .GroupBy(j => j.ProductId)
            .Select(g => new { ProductId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(r => r.ProductId, r => r.Count);
    }
}
