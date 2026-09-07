using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;

namespace Plantry.Pantry.Infrastructure;

public sealed class ProductStockRepository(PantryDbContext db, ILogger<ProductStockRepository>? logger = null) : IProductStockRepository
{
    public async Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default)
    {
        // Authoritative write-serialization for a multi-lot consume: take a row lock on the
        // product_stock root before loading its lots (inventory.md resolved-call #1). The RLS
        // query filter still applies to the FromSql root set, so this stays household-scoped.
        // xmin is a hidden system column that SELECT * does not project; EF composes over this
        // FromSql and reads the mapped xmin row-version, so it must be named explicitly.
        var root = await db.ProductStocks
            .FromSql($"SELECT *, xmin FROM inventory.product_stock WHERE household_id = {householdId.Value} AND product_id = {productId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (root is null) return null;

        // Bring the lots and journal into the tracked graph (the FromSql above loads only the
        // root row). The journal is needed so ProductStock.Consume can perform the sourceLineRef
        // idempotency check against already-applied tokens (plantry-292a).
        await db.Entry(root).Collection(p => p.Entries).LoadAsync(ct);
        await db.Entry(root).Collection(p => p.Journal).LoadAsync(ct);
        return root;
    }

    public Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        db.ProductStocks
            .Include(p => p.Entries)
            .FirstOrDefaultAsync(p => p.HouseholdId == householdId && p.ProductId == productId, ct);

    public Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        db.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .FirstOrDefaultAsync(p => p.HouseholdId == householdId && p.ProductId == productId, ct);

    public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        db.ProductStocks
            .Include(p => p.Entries)
            .Where(p => p.HouseholdId == householdId)
            .ToListAsync(ct);

    public async Task<IReadOnlySet<Guid>> ListProductIdsWithStockAsync(
        HouseholdId householdId, IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var idList = productIds.ToList();
        var found = await db.ProductStocks
            .Where(p => p.HouseholdId == householdId && idList.Contains(p.ProductId))
            .Select(p => p.ProductId)
            .ToListAsync(ct);
        return found.ToHashSet();
    }

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        db.ProductStocks
            .AnyAsync(p => p.HouseholdId == householdId, ct);

    public async Task AddAsync(ProductStock stock, CancellationToken ct = default) =>
        await db.ProductStocks.AddAsync(stock, ct);

    public async Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default)
    {
        // A concurrent first-ever-stock race is expected here and handled by the caller
        // (RecordCountCommand falls back to the delta path). But when this call runs inside an
        // ambient transaction (ExecuteInTransactionAsync's caller — RecordCountCommand's
        // first-stock branch), a failed INSERT aborts the whole Postgres transaction: the
        // fallback FindForUpdateAsync that follows would then fail too. A savepoint around just
        // the insert attempt lets us roll back to before the failed INSERT while keeping the
        // outer transaction (and its row locks) alive for the fallback to use.
        var tx = db.Database.CurrentTransaction;
        var savepointName = $"try_add_{Guid.NewGuid():N}";
        if (tx is not null)
            await tx.CreateSavepointAsync(savepointName, ct);

        try
        {
            await db.ProductStocks.AddAsync(stock, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (tx is not null)
            {
                // Best-effort: if the savepoint rollback itself throws (broken connection, a
                // cancelled ct mid-ROLLBACK), the transaction is already unusable either way — the
                // caller's own failure path (RecordCountCommand's outer ExecuteInTransactionAsync
                // rolling back on the eventual failed Result, or the ambient BeginTransactionAsync
                // disposing without commit) still cleans up. What must not happen is this
                // documented "returns false on the race" contract turning into an unrelated
                // exception surfacing instead (plantry-bxzh FIX pass 1).
                try { await tx.RollbackToSavepointAsync(savepointName, ct); }
                catch (Exception rollbackEx)
                {
                    logger?.LogWarning(rollbackEx,
                        "Savepoint rollback failed after a duplicate-key insert for product {ProductId}; the ambient transaction is unusable and the caller's own failure path will clean up.",
                        stock.ProductId);
                }
            }
            return false;
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        T result;
        try
        {
            result = await work(ct);
        }
        catch
        {
            // Best-effort: never let a failure inside RollbackAsync itself (a cancelled ct mid-
            // ROLLBACK, a broken connection) replace the real exception the caller needs to see —
            // that would silently turn "the delegate failed" into "the rollback failed" (plantry-bxzh
            // FIX pass 1). The `await using var tx` disposal below still rolls back without a token
            // if this best-effort attempt could not.
            try { await tx.RollbackAsync(ct); }
            catch (Exception rollbackEx)
            {
                logger?.LogWarning(rollbackEx,
                    "Transaction rollback failed while unwinding a failed ExecuteInTransactionAsync delegate; rethrowing the original exception.");
            }
            throw;
        }

        // Commands return Result/Result<T> failures rather than throwing, so a write that
        // flushed inside the delegate before the failure was detected must not be committed —
        // otherwise the caller sees an error while a partial effect is left durable
        // (plantry-bxzh). RollbackAsync is a no-op if nothing was actually written.
        if (result is IResultOutcome { IsFailure: true })
        {
            await tx.RollbackAsync(ct);
            return result;
        }

        await tx.CommitAsync(ct);
        return result;
    }
}
