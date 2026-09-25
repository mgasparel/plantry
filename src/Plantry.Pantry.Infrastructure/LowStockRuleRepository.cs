using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;

namespace Plantry.Pantry.Infrastructure;

/// <summary>
/// EF-backed <see cref="ILowStockRuleRepository"/> over <see cref="PantryDbContext"/>. The context's
/// per-household query filter plus the table's RLS policy scope every read to the tenant
/// (defence-in-depth, ADR-008).
/// </summary>
public sealed class LowStockRuleRepository(PantryDbContext db, ILogger<LowStockRuleRepository>? logger = null)
    : ILowStockRuleRepository
{
    public Task<LowStockRule?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        db.LowStockRules
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.ProductId == productId, ct);

    public async Task<IReadOnlyDictionary<Guid, LowStockRule>> ListForHouseholdAsync(
        HouseholdId householdId, CancellationToken ct = default)
    {
        var rules = await db.LowStockRules
            .Where(r => r.HouseholdId == householdId)
            .ToListAsync(ct);
        return rules.ToDictionary(r => r.ProductId);
    }

    public async Task AddAsync(LowStockRule rule, CancellationToken ct = default) =>
        await db.LowStockRules.AddAsync(rule, ct);

    public async Task<bool> TryAddAndSaveAsync(LowStockRule rule, CancellationToken ct = default)
    {
        // A concurrent first-create race is expected here (e.g. a double-submitted threshold-sheet
        // POST) and handled by the caller. When this call runs inside an ambient transaction, a failed
        // INSERT aborts the whole Postgres transaction; a savepoint around just the insert attempt lets
        // us roll back to before the failed INSERT while keeping the outer transaction alive — mirrors
        // ProductStockRepository.TryAddAndSaveAsync exactly.
        var tx = db.Database.CurrentTransaction;
        var savepointName = $"try_add_{Guid.NewGuid():N}";
        if (tx is not null)
            await tx.CreateSavepointAsync(savepointName, ct);

        try
        {
            await db.LowStockRules.AddAsync(rule, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (tx is not null)
            {
                try { await tx.RollbackToSavepointAsync(savepointName, ct); }
                catch (Exception rollbackEx)
                {
                    logger?.LogWarning(rollbackEx,
                        "Savepoint rollback failed after a duplicate-key insert for low-stock rule, product {ProductId}; the ambient transaction is unusable and the caller's own failure path will clean up.",
                        rule.ProductId);
                }
            }
            return false;
        }
    }

    public Task RemoveAsync(LowStockRule rule, CancellationToken ct = default)
    {
        db.LowStockRules.Remove(rule);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
