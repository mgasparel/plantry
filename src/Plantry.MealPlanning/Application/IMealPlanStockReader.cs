namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto Inventory for the MealPlanning context (domain-model §8, DM-13).
/// Reuses the same contract as <c>Plantry.Recipes.Application.IInventoryStockReader</c> but is
/// owned by MealPlanning.Application so MealPlanning never depends on the Recipes assembly.
/// Implemented in Plantry.Web over the same <c>InventoryQueryService</c> adapter.
/// </summary>
public interface IMealPlanStockReader
{
    /// <summary>
    /// Returns stock snapshot (available quantity in the product's default unit and soonest expiry)
    /// for a single product. Null when the product has no stock record.
    /// </summary>
    Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default);
}

/// <summary>
/// Point-in-time stock snapshot for one product, in that product's default unit.
/// Mirrors <c>Plantry.Recipes.Application.ProductStock</c> — a separate copy per context (DM-3).
/// </summary>
/// <param name="ProductId">The product this snapshot covers.</param>
/// <param name="AvailableQuantity">Available quantity in the product's default unit.</param>
/// <param name="DefaultUnitId">Unit in which <see cref="AvailableQuantity"/> is expressed.</param>
/// <param name="SoonestExpiry">Earliest expiry across all active lots; null when no expiry.</param>
public sealed record MealPlanProductStock(
    Guid ProductId,
    decimal AvailableQuantity,
    Guid DefaultUnitId,
    DateOnly? SoonestExpiry);
