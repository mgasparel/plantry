using Plantry.SharedKernel;

namespace Plantry.Pantry.Domain;

/// <summary>
/// Persistence port for <see cref="LowStockRule"/>. Implemented in Plantry.Pantry.Infrastructure.
/// </summary>
public interface ILowStockRuleRepository
{
    /// <summary>Returns the rule for this household/product pair, or null when no threshold is set.</summary>
    Task<LowStockRule?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default);

    /// <summary>All rules for the household, keyed by product id — the batch call read models use to join
    /// thresholds onto a product list in one round trip (no N+1).</summary>
    Task<IReadOnlyDictionary<Guid, LowStockRule>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);

    Task AddAsync(LowStockRule rule, CancellationToken ct = default);

    /// <summary>
    /// Adds and saves <paramref name="rule"/> as a new row in one step, handling a concurrent
    /// first-create race on the <c>(household_id, product_id)</c> primary key (mirrors
    /// <see cref="IProductStockRepository.TryAddAndSaveAsync"/>). Returns true on success; returns
    /// false if another request already inserted a rule for the same pair. On false the caller should
    /// reload via <see cref="FindAsync"/> and apply its change to the existing row instead.
    /// </summary>
    Task<bool> TryAddAndSaveAsync(LowStockRule rule, CancellationToken ct = default);

    Task RemoveAsync(LowStockRule rule, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
