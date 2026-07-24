using Microsoft.Extensions.Logging;
using Plantry.Intake.Application;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="IAmendStockPort"/> — amends an already-committed purchase's lot over
/// the Inventory <see cref="AmendPurchaseCommand"/> (ADR-023 A10). Unlike <see cref="AddStockAdapter"/>,
/// this does NOT throw on failure: Inventory's amendment guards are expected, user-facing outcomes (spec
/// acceptance #3/#4), so the <see cref="Result{T}"/> is passed straight through for
/// <c>AmendCommittedLineCommand</c> to surface verbatim.
/// </summary>
public sealed class AmendStockAdapter(
    IProductStockRepository stocks,
    IClock clock,
    ITenantContext tenant,
    ILogger<AmendPurchaseCommand> logger) : IAmendStockPort
{
    public Task<Result<decimal>> AmendAsync(
        Guid productId, Guid stockEntryId, decimal correctedQuantity, Guid importLineId, Guid userId,
        CancellationToken ct = default)
    {
        var command = new AmendPurchaseCommand(
            productId, stockEntryId, correctedQuantity, importLineId, userId, stocks, clock, tenant, logger);

        return command.ExecuteAsync(ct);
    }
}
