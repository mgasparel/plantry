namespace Plantry.Intake.Application;

/// <summary>
/// Port: cross-context call to Inventory to add a stock lot during commit.
/// Implemented in Plantry.Web (adapter over Inventory's AddStockCommand).
/// Returns the StockEntryId (as Guid) of the created lot.
/// </summary>
public interface IAddStockPort
{
    /// <param name="sourceRef">
    /// The committing <see cref="Plantry.Intake.Domain.ImportLine"/>'s id (receipt-intake-history.md H1) —
    /// stamped as the resulting journal row's <c>SourceRef</c> so a pantry history row can resolve straight
    /// back to the receipt line that produced it. Null for the rare commit path with no line to attribute.
    /// </param>
    Task<Guid> AddStockAsync(
        Guid productId,
        Guid? skuId,
        decimal quantity,
        Guid unitId,
        Guid locationId,
        DateOnly? expiryDate,
        DateOnly? purchasedAt,
        Guid userId,
        Guid? sourceRef = null,
        CancellationToken ct = default);
}
