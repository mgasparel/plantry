using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;

namespace Plantry.Pantry.Infrastructure;

/// <summary>
/// EF-backed <see cref="IWasteJournalReader"/> (plantry-h9z9). Mirrors <see cref="PurchaseJournalReader"/>'s
/// shape: both queries filter <c>stock_journal_entry</c> by <c>Reason</c> and aggregate entirely in the
/// database (a <c>COUNT</c> and a <c>MAX</c>-equivalent order-then-take-one), so no journal rows or
/// <c>ProductStock</c> aggregates are materialized. The <c>household_id</c> index on the journal covers both
/// queries; household scoping is handled by <see cref="PantryDbContext"/>'s RLS query filter (armed per
/// request by <c>RlsMiddleware</c>), the same way <see cref="PurchaseJournalReader"/> is scoped.
/// </summary>
public sealed class WasteJournalReader(PantryDbContext db) : IWasteJournalReader
{
    public Task<int> CountDiscardedSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        db.StockJournalEntries
            .CountAsync(j => j.Reason == StockReason.Discarded && j.OccurredAt >= since, ct);

    public Task<DateTimeOffset?> MostRecentDiscardAsync(CancellationToken ct = default) =>
        db.StockJournalEntries
            .Where(j => j.Reason == StockReason.Discarded)
            .OrderByDescending(j => j.OccurredAt)
            .Select(j => (DateTimeOffset?)j.OccurredAt)
            .FirstOrDefaultAsync(ct);
}
