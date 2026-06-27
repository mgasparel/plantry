using Microsoft.Extensions.Logging;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="IInventoryConsumer"/> — delegates to Inventory's single
/// consumption primitive (<see cref="ConsumeStockCommand"/> / <see cref="ProductStock.Consume"/>)
/// with <see cref="StockSourceType.Cook"/> and the cook event id as the source reference, so every
/// journal row written during a cook is traceable back to the originating <c>CookEvent</c> (ADR-011,
/// recipes-domain-model.md §8). Lives in Plantry.Web, the composition root that references both
/// contexts; the Recipes projects stay <c>→ SharedKernel only</c>.
///
/// FEFO multi-lot deduction and unit conversion are handled entirely by <c>ProductStock.Consume</c>
/// (mutation-tested in Plantry.Tests.Unit) — this adapter does not reimplement them.
/// </summary>
public sealed class InventoryConsumerAdapter(
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant,
    ILogger<ConsumeStockCommand> consumeLogger) : IInventoryConsumer
{
    public async Task<ConsumeResult> ConsumeAsync(
        Guid productId,
        decimal quantity,
        Guid unitId,
        ConsumeReason reason,
        Guid cookEventId,
        Guid userId,
        Guid sourceLineRef,
        CancellationToken ct = default)
    {
        var stockReason = reason switch
        {
            ConsumeReason.Recipe => StockReason.Consumed,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown ConsumeReason."),
        };

        var command = new ConsumeStockCommand(
            productId,
            quantity,
            unitId,
            stockReason,
            userId,
            targetEntryId: null,
            sourceRef: cookEventId,
            stocks,
            conversions,
            clock,
            tenant,
            StockSourceType.Cook,
            sourceLineRef: sourceLineRef,
            logger: consumeLogger);

        var result = await command.ExecuteAsync(ct);

        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Inventory consume failed ({result.Error.Code}): {result.Error.Description}");

        var outcome = result.Value;
        return new ConsumeResult(outcome.ShortfallAmount, outcome.RequestUnitId);
    }
}
