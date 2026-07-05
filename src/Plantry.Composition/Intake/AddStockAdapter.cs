using Plantry.Intake.Application;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="IAddStockPort"/> — records a purchased lot during intake commit over
/// the Inventory <see cref="AddStockCommand"/>, stamped <see cref="StockSourceType.Intake"/> so the
/// journal attributes it to the receipt flow. Throws on failure so the per-line commit can abort that
/// line without marking it committed.
/// </summary>
public sealed class AddStockAdapter(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IClock clock,
    ITenantContext tenant) : IAddStockPort
{
    public async Task<Guid> AddStockAsync(
        Guid productId, Guid? skuId, decimal quantity, Guid unitId, Guid locationId,
        DateOnly? expiryDate, DateOnly? purchasedAt, Guid userId, CancellationToken ct = default)
    {
        var command = new AddStockCommand(
            productId, quantity, unitId, locationId, userId,
            skuId: skuId, expiryDate: expiryDate, purchasedAt: purchasedAt,
            stocks, catalog, clock, tenant, StockSourceType.Intake);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Add stock failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }
}
