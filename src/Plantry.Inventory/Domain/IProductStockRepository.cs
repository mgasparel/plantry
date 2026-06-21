using Plantry.SharedKernel;

namespace Plantry.Inventory.Domain;

/// <summary>
/// Persistence port for the <see cref="ProductStock"/> aggregate. Implemented in
/// Plantry.Inventory.Infrastructure.
/// </summary>
public interface IProductStockRepository
{
    /// <summary>
    /// Loads the aggregate (with its lots) taking a <c>SELECT … FOR UPDATE</c> row lock on the
    /// <c>product_stock</c> root — the authoritative write-serialization for a multi-lot consume
    /// (inventory.md resolved-call #1). Returns null if the product has no stock yet.
    /// </summary>
    Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default);

    /// <summary>Loads the aggregate with its lots for read-only use (no lock).</summary>
    Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default);

    /// <summary>Loads the aggregate with both its lots and its journal history — feeds the product detail read model.</summary>
    Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default);

    /// <summary>All product-stock aggregates (with lots) for <paramref name="householdId"/> — feeds the pantry read model.</summary>
    Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the household has at least one product-stock record — used for the
    /// Today-page cold-start check to avoid materializing the full list.
    /// </summary>
    Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);

    Task AddAsync(ProductStock stock, CancellationToken ct = default);

    /// <summary>
    /// Adds and saves <paramref name="stock"/> as a new root in one step, handling concurrent
    /// first-intake races. Returns true on success; returns false if another request already
    /// inserted the same <c>(householdId, productId)</c> key. On false the caller should
    /// reload via <see cref="FindAsync"/> and add the lot to the existing root.
    /// </summary>
    Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="work"/> inside a single database transaction so the row lock taken by
    /// <see cref="FindForUpdateAsync"/> is held through <see cref="SaveChangesAsync"/> (a bare
    /// <c>FOR UPDATE</c> would otherwise release at autocommit). Commits on success, rolls back on
    /// throw. In-memory fakes may simply invoke <paramref name="work"/> inline.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default);
}
