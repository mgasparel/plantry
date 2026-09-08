using Plantry.SharedKernel;

namespace Plantry.Pantry.Domain;

/// <summary>
/// Persistence port for the <see cref="ProductStock"/> aggregate. Implemented in
/// Plantry.Pantry.Infrastructure.
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
    /// The subset of <paramref name="productIds"/> that hold a stock record for
    /// <paramref name="householdId"/> — a lean existence projection (no aggregate/lot materialization)
    /// for batch pantry-vs-catalog product-link resolution (e.g. the Intake Session detail line grid,
    /// plantry-ubqb). Ids without a stock record are simply absent from the result.
    ///
    /// <para>The default implementation falls back to a per-id <see cref="FindAsync"/> loop so test
    /// doubles need not reimplement it; the EF repository overrides it with one projected query.</para>
    /// </summary>
    async Task<IReadOnlySet<Guid>> ListProductIdsWithStockAsync(
        HouseholdId householdId, IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var result = new HashSet<Guid>();
        foreach (var productId in productIds)
            if (await FindAsync(householdId, productId, ct) is not null)
                result.Add(productId);
        return result;
    }

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
    /// <c>FOR UPDATE</c> would otherwise release at autocommit). Commits when the delegate
    /// completes and its return value is not a failed <c>Result</c>/<c>Result&lt;T&gt;</c>; rolls
    /// back on a throw AND on a returned failure, so a write flushed before a failure was
    /// detected never survives a reported error (plantry-bxzh). In-memory fakes may simply invoke
    /// <paramref name="work"/> inline — they therefore do NOT model the rollback-on-failed-Result
    /// behaviour, so that behaviour must be proven at L3 (see
    /// <c>tests/Plantry.Tests.Integration/Inventory/ExecuteInTransactionRollbackTests.cs</c>).
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default);
}
