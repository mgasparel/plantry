namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto Inventory for the MealPlanning insights engine (P3-5, DM-13).
/// Returns the set of product IDs whose stock is expiring within the "Use soon" window so the
/// insights engine can identify expiring stock that no planned dish will consume.
/// Implemented in Plantry.Web over InventoryQueryService / IProductStockRepository.
/// Lives in MealPlanning.Application so MealPlanning never takes a compile-time dependency on Inventory.
/// </summary>
public interface IMealPlanExpiringStockReader
{
    /// <summary>
    /// Returns the product IDs of every product whose active stock has at least one lot expiring
    /// within <paramref name="withinDays"/> days of <paramref name="today"/>.
    /// Returns an empty collection when no stock is expiring.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetExpiringProductIdsAsync(
        DateOnly today,
        int withinDays,
        CancellationToken ct = default);
}
