using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.MealPlanning.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Composition-root adapter for <see cref="IMealPlanEatWriter"/> (plantry-zcbx) — delegates to
/// Inventory's single consumption primitive (<see cref="ConsumeStockCommand"/>) for Eat and its
/// intake counterpart (<see cref="AddStockCommand"/>) for the compensating Undo add, both stamped
/// <see cref="StockSourceType.Eat"/> with <c>SourceRef</c> = the planned dish id — the exact seam
/// <c>IMealPlanCookStatusReader</c>'s product-dish netting reads back. Lives in the composition root
/// (which references both contexts); MealPlanning.Application stays free of any Inventory dependency.
///
/// <para>
/// <b>Idempotency / re-eat token scheme.</b> <c>SourceLineRef</c> on each journal row is a
/// deterministic <see cref="Guid"/> derived from (<c>plannedDishId</c>, an ordinal <c>n</c>,
/// "eat"|"eat-undo") via a truncated SHA-256 digest — never a randomly-generated id — so two racing
/// requests that read the same "before" state independently compute the exact same token. <c>n</c> is
/// the count of prior EAT journal rows (negative <c>Delta</c>) for the dish, read fresh on every call
/// via <see cref="IJournalEntriesBySourceRefReader"/> (no MealPlanning-side counter, nothing persisted
/// beyond the journal itself):
/// <list type="bullet">
/// <item>Eat n: token = hash(dishId, n, "eat"). Two simultaneous eat taps both compute n = (prior eat
/// count) + 1 and thus the same token — the second racer's <see cref="ProductStock.Consume"/> call
/// finds the first's journal row already carrying that token and short-circuits to a no-op
/// (the same (SourceRef, SourceLineRef) guard the Recipes cook flow relies on, plantry-292a/fks).</item>
/// <item>Undo of eat n: token = hash(dishId, n, "eat-undo"), where n = the CURRENT eat count (i.e. the
/// latest eat's own n) — so undo always targets the most recent eat, and a double-tapped undo
/// dedupes via <see cref="ProductStock.AddStock"/>'s matching (SourceRef, SourceLineRef) guard.</item>
/// <item>A re-eat after an undo simply computes a fresh, larger n (the undo's compensating ADD is a
/// positive-delta row, not a negative one, so it never counts toward "prior eat count") — a brand new
/// token, a brand new journal row.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Undo restores what was actually deducted, not the blanket requested quantity.</b> When the
/// original eat was shortfall-tolerant (stock ran out partway through), re-issuing the SAME consume
/// call with the SAME eat-n token is itself idempotent and, per <see cref="ProductStock.Consume"/>'s
/// own re-drive contract, returns the shortfall recomputed from the matching journal rows instead of
/// mutating anything — exactly the mechanism <c>ReconcilePendingCooks</c> already relies on. Undo
/// replays that call purely to learn the true shortfall, then compensates by (requested − shortfall):
/// the exact amount the original eat removed, never more.
/// </para>
/// </summary>
public sealed class MealPlanEatWriterAdapter(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
    IJournalEntriesBySourceRefReader journalReader,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant,
    ILogger<ConsumeStockCommand> consumeLogger,
    ILogger<AddStockCommand> addLogger) : IMealPlanEatWriter
{
    public async Task EatAsync(
        Guid plannedDishId, Guid productId, decimal quantity, Guid userId, CancellationToken ct = default)
    {
        var unitId = await ResolveDefaultUnitAsync(productId, ct);
        var priorEats = await CountEatsAsync(plannedDishId, ct);
        var token = Token(plannedDishId, priorEats + 1, "eat");

        var command = new ConsumeStockCommand(
            productId, quantity, unitId, StockReason.Consumed, userId,
            targetEntryId: null, sourceRef: plannedDishId,
            stocks, catalog, conversions, clock, tenant,
            StockSourceType.Eat, sourceLineRef: token, logger: consumeLogger);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
        {
            if (result.Error.Code == "Inventory.NoStock")
            {
                // Shortfall tolerance extends to the total-shortfall case (C8/R9 mirror): a product
                // that has never been stocked has nothing to consume, so the call completes without
                // throwing rather than surfacing a 500 for a one-tap action. No journal row is written
                // (there is nothing to net negative), so the dish's derived state legitimately stays
                // pending — consistent with ProductStock.Consume's own behaviour when a stock record
                // exists but every lot is already depleted (zero candidates, zero journal rows, still
                // a graceful success).
                return;
            }

            throw new InvalidOperationException($"Eat consume failed ({result.Error.Code}): {result.Error.Description}");
        }
    }

    public async Task UndoEatAsync(
        Guid plannedDishId, Guid productId, decimal quantity, Guid userId, CancellationToken ct = default)
    {
        var eatCount = await CountEatsAsync(plannedDishId, ct);
        if (eatCount == 0)
            return; // nothing outstanding to undo

        if (tenant.HouseholdId is not { } householdGuid)
            return; // unauthenticated/no-tenant — nothing to do at this port boundary
        var household = HouseholdId.From(householdGuid);

        var unitId = await ResolveDefaultUnitAsync(productId, ct);
        var eatToken = Token(plannedDishId, eatCount, "eat");

        // Replay the eat consume with the SAME token. ProductStock.Consume's idempotency guard
        // short-circuits (nothing is mutated) and reports the shortfall recomputed from the matching
        // journal rows — the sanctioned way (plantry-fks) to learn exactly how much a prior consume
        // actually removed, even when it was partially or fully shorted.
        var replay = new ConsumeStockCommand(
            productId, quantity, unitId, StockReason.Consumed, userId,
            targetEntryId: null, sourceRef: plannedDishId,
            stocks, catalog, conversions, clock, tenant,
            StockSourceType.Eat, sourceLineRef: eatToken, logger: consumeLogger);
        var replayResult = await replay.ExecuteAsync(ct);
        if (replayResult.IsFailure)
            return; // the eat never actually recorded anything (e.g. no-stock at eat time) — nothing to undo

        var actuallyDeducted = quantity - replayResult.Value.ShortfallAmount;
        if (actuallyDeducted <= 0m)
            return; // the eat was a full shortfall — nothing was ever removed, nothing to restore

        var activeLocations = await locations.ListActiveAsync(ct);
        if (activeLocations.Count == 0)
            throw new InvalidOperationException("Cannot undo eat — the household has no active storage location.");
        var locationId = activeLocations[0].Id.Value;
        var undoToken = Token(plannedDishId, eatCount, "eat-undo");

        // Bypass the shared AddStockCommand (it hard-codes reason = Purchase — wrong here; an undo is
        // a compensating fix to the record, not new spend, so Correction is the correct reason, same
        // rationale as Take Stock's C8 opening balance). Mirrors TakeStockCommands' direct-domain-call
        // pattern: FindForUpdateAsync's row lock serializes a racing double-tapped undo — the loser
        // blocks, then (once it proceeds) AddStock's own (SourceRef, SourceLineRef) guard finds the
        // winner's row already there and short-circuits to a no-op, so a double-POST never double-adds.
        await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);
            if (stock is null)
            {
                addLogger.LogWarning(
                    "Eat undo skipped — no stock record for product {ProductId} (plan dish {DishId}).",
                    productId, plannedDishId);
                return false; // the eat's own consume must have created this root; nothing to restore if it's gone
            }

            stock.AddStock(
                actuallyDeducted, unitId, locationId, userId, clock,
                sourceType: StockSourceType.Eat, sourceRef: plannedDishId, sourceLineRef: undoToken,
                reason: StockReason.Correction);

            await stocks.SaveChangesAsync(innerCt);

            addLogger.LogInformation(
                "Eat undone for product {ProductId} (plan dish {DishId}). Quantity restored: {Quantity}.",
                productId, plannedDishId, actuallyDeducted);

            return true;
        }, ct);
    }

    private async Task<Guid> ResolveDefaultUnitAsync(Guid productId, CancellationToken ct)
    {
        var product = await catalog.FindProductAsync(productId, ct);
        if (product is null)
            throw new InvalidOperationException($"Eat failed — product {productId} does not exist in Catalog.");
        return product.DefaultUnitId;
    }

    /// <summary>Count of prior EAT journal rows (negative <c>Delta</c>) for this dish — undo (ADD) rows never count.</summary>
    private async Task<int> CountEatsAsync(Guid plannedDishId, CancellationToken ct)
    {
        var movements = await journalReader.ListBySourceRefsAsync([plannedDishId], ct);
        return movements.TryGetValue(plannedDishId, out var list) ? list.Count(m => m.Delta < 0) : 0;
    }

    /// <summary>
    /// Deterministic (never random) idempotency token so two racing requests that observe the same
    /// "before" state compute the identical <see cref="Guid"/> — the truncated-SHA-256 pattern already
    /// used for order-independent hashing elsewhere in the codebase (<c>Recipe.IngredientProductHash</c>).
    /// </summary>
    private static Guid Token(Guid plannedDishId, int n, string kind)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"mealplan-{kind}:{plannedDishId:N}:{n}"));
        return new Guid(digest[..16]);
    }
}
