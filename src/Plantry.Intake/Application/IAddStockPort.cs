namespace Plantry.Intake.Application;

/// <summary>
/// Port: cross-context call to Inventory to add a stock lot during commit.
/// Implemented in Plantry.Web (adapter over Inventory's AddStockCommand).
/// Returns the StockEntryId (as Guid) of the created lot.
/// </summary>
public interface IAddStockPort
{
    Task<Guid> AddStockAsync(
        Guid productId,
        Guid? skuId,
        decimal quantity,
        Guid unitId,
        Guid locationId,
        DateOnly? expiryDate,
        DateOnly? purchasedAt,
        Guid userId,
        CancellationToken ct = default);
}
