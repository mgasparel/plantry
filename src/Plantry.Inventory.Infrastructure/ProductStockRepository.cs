using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Inventory.Infrastructure;

public sealed class ProductStockRepository(InventoryDbContext db) : IProductStockRepository
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

    public async Task AddAsync(ProductStock stock, CancellationToken ct = default) =>
        await db.ProductStocks.AddAsync(stock, ct);

    public async Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default)
    {
        try
        {
            await db.ProductStocks.AddAsync(stock, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work(ct);
        await tx.CommitAsync(ct);
        return result;
    }
}
