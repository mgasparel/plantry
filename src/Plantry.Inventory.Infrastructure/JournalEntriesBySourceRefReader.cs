using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Application;

namespace Plantry.Inventory.Infrastructure;

/// <summary>
/// EF-backed <see cref="IJournalEntriesBySourceRefReader"/> (plantry-0eut). One batched query over
/// <c>stock_journal_entry</c> filtered to <c>SourceRef IN (@sourceRefs)</c>, translated as an
/// <c>ANY(@p)</c> array predicate against the <c>ix_stock_journal_idempotency (household_id, source_ref,
/// source_line_ref)</c> index. Household scoping is handled by <see cref="InventoryDbContext"/>'s RLS
/// query filter, the same way <see cref="PurchaseJournalReader"/> is scoped — so this reader carries no
/// household argument.
/// </summary>
public sealed class JournalEntriesBySourceRefReader(InventoryDbContext db) : IJournalEntriesBySourceRefReader
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<JournalMovement>>> ListBySourceRefsAsync(
        IReadOnlyCollection<Guid> sourceRefs, CancellationToken ct = default)
    {
        if (sourceRefs.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<JournalMovement>>();

        // Materialise to a list so EF translates the Contains as an ANY(@p) array predicate.
        var wanted = sourceRefs as IReadOnlyList<Guid> ?? sourceRefs.ToList();

        var rows = await db.StockJournalEntries
            .Where(j => j.SourceRef != null && wanted.Contains(j.SourceRef!.Value))
            .Select(j => new { SourceRef = j.SourceRef!.Value, j.Delta, j.OccurredAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.SourceRef)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<JournalMovement>)g.Select(r => new JournalMovement(r.Delta, r.OccurredAt)).ToList());
    }
}
