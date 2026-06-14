using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Inventory.Application;

/// <summary>
/// Manual intake (SPEC §2c). Loads-or-starts the product's <see cref="ProductStock"/> and records a
/// new lot via <see cref="ProductStock.AddStock"/> with <see cref="StockSourceType.Manual"/>. The
/// expiry is materialized at the page boundary (DM-11) and passed in already-resolved; the command
/// stores whatever date it is given. Intake from the AI pipeline (Slice 6) reuses the same aggregate.
/// </summary>
public sealed class AddStockCommand(
    Guid productId,
    decimal quantity,
    Guid unitId,
    Guid locationId,
    Guid userId,
    Guid? skuId,
    DateOnly? expiryDate,
    DateOnly? purchasedAt,
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IClock clock,
    ITenantContext tenant,
    StockSourceType sourceType = StockSourceType.Manual)
{
    public async Task<Result<StockEntryId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (quantity <= 0m)
            return Error.Custom("Inventory.InvalidQuantity", "Quantity must be greater than zero.");

        var product = await catalog.FindProductAsync(productId, ct);
        if (product is null)
            return Error.Custom("Inventory.UnknownProduct", "The selected product does not exist.");
        if (!product.CanHoldStock)
            return Error.Custom("Inventory.ProductCannotHoldStock", "A parent product cannot hold stock directly; choose a variant.");

        var household = HouseholdId.From(householdId);
        var stock = await stocks.FindAsync(household, productId, ct);
        var isNew = stock is null;
        stock ??= ProductStock.Start(household, productId, clock);

        var entry = stock.AddStock(
            quantity, unitId, locationId, userId, clock,
            skuId: skuId, expiryDate: expiryDate, purchasedAt: purchasedAt,
            sourceType: sourceType);

        if (isNew)
        {
            if (!await stocks.TryAddAndSaveAsync(stock, ct))
            {
                // Concurrent first-intake race: another request won the root insert.
                // Reload and re-add the lot to the existing root.
                stock = (await stocks.FindAsync(household, productId, ct))!;
                entry = stock.AddStock(
                    quantity, unitId, locationId, userId, clock,
                    skuId: skuId, expiryDate: expiryDate, purchasedAt: purchasedAt,
                    sourceType: sourceType);
                await stocks.SaveChangesAsync(ct);
            }
        }
        else
        {
            await stocks.SaveChangesAsync(ct);
        }

        return entry.Id;
    }
}

/// <summary>
/// The single consumption entry point for the UI (SPEC §1c/§1d) — used (<c>Consumed</c>), wasted
/// (<c>Discarded</c>), or manually corrected (<c>Correction</c>). Resolves the product's converter
/// through the <see cref="IProductConversionProvider"/> port, loads the root under a row lock, and
/// runs <see cref="ProductStock.Consume"/> inside a transaction so the lock serializes concurrent
/// consumes (DM-13). Returns the <see cref="ConsumeOutcome"/> so the UI can surface any shortfall.
/// </summary>
public sealed class ConsumeStockCommand(
    Guid productId,
    decimal amount,
    Guid unitId,
    StockReason reason,
    Guid userId,
    Guid? targetEntryId,
    Guid? sourceRef,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant,
    StockSourceType sourceType = StockSourceType.Manual)
{
    public async Task<Result<ConsumeOutcome>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (!reason.IsRemoval())
            return Error.Custom("Inventory.InvalidConsumeReason", "Consume cannot record a Purchase; use AddStock.");

        var converter = await conversions.ForProductAsync(productId, ct);
        var household = HouseholdId.From(householdId);

        return await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);
            if (stock is null)
                return Result<ConsumeOutcome>.Failure(Error.Custom("Inventory.NoStock", "There is no stock for this product."));

            var outcome = stock.Consume(
                amount, unitId, reason, converter, userId, clock,
                sourceRef: sourceRef,
                sourceType: sourceType,
                targetEntry: targetEntryId is { } id ? StockEntryId.From(id) : null);

            if (outcome.IsFailure)
                return outcome; // nothing mutated (planning pass failed) — commits as a no-op

            await stocks.SaveChangesAsync(innerCt);
            return outcome;
        }, ct);
    }
}
