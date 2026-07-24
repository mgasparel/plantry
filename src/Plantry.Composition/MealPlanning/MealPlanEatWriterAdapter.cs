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
/// <item>Undo of eat n: one compensating ADD row per lot the eat actually touched, each token =
/// hash(dishId, n, "eat-undo:{lotId}") — where n = the CURRENT eat count (i.e. the latest eat's own
/// n) — so undo always targets the most recent eat, and a double-tapped undo dedupes each row
/// independently via <see cref="ProductStock.AddStock"/>'s matching (SourceRef, SourceLineRef)
/// guard.</item>
/// <item>A re-eat after an undo simply computes a fresh, larger n (the undo's compensating ADD is a
/// positive-delta row, not a negative one, so it never counts toward "prior eat count") — a brand new
/// token, a brand new journal row.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Undo restores exactly what was actually deducted, in each lot's own unit — never a blanket ADD
/// in the product's default unit.</b> <see cref="ProductStock.Consume"/> writes one negative journal
/// row per lot it actually touched, each stamped with that lot's own unit; when the eat was
/// shortfall-tolerant (stock ran out partway through), a lot that was never reached simply has no
/// row. Undo re-reads the aggregate's own journal for the rows carrying the eat's token and, for each
/// one, appends a compensating ADD of that row's exact (quantity, unit) — the amount the eat removed
/// from THAT lot, never more, never converted through the product's default unit. This matters
/// because <c>MealPlanCookStatusReaderAdapter</c> nets a dish's journal rows by summing raw
/// <c>Delta</c> with no unit conversion: restoring via the default unit would fix the physical stock
/// but, whenever the default unit differs from a lot's unit, leave that raw net non-zero and the dish
/// stuck "eaten" (plantry-wiv2). Mirroring each row's own unit makes the net return to exactly zero.
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

        var eatToken = Token(plannedDishId, eatCount, "eat");

        var activeLocations = await locations.ListActiveAsync(ct);
        if (activeLocations.Count == 0)
            throw new InvalidOperationException("Cannot undo eat — the household has no active storage location.");
        var locationId = activeLocations[0].Id.Value;

        // Bypass the shared AddStockCommand (it hard-codes reason = Purchase — wrong here; an undo is
        // a compensating fix to the record, not new spend, so Correction is the correct reason, same
        // rationale as Take Stock's C8 opening balance). Mirrors TakeStockCommands' direct-domain-call
        // pattern: FindForUpdateAsync's row lock serializes a racing double-tapped undo — the loser
        // blocks, then (once it proceeds) each row's own (SourceRef, SourceLineRef) guard finds the
        // winner's rows already there and short-circuits every one of them to a no-op, so a
        // double-POST never double-adds.
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

            // Compensate PER the original eat's own journal rows, each restored in that row's OWN
            // unit — never a single ADD in the product's default unit (plantry-wiv2). Consume writes
            // one negative journal row per lot touched, each stamped with that lot's own unit
            // (ProductStock.Consume). MealPlanCookStatusReaderAdapter nets a dish's journal rows by
            // summing raw Delta with NO unit conversion, so when the product's default unit differs
            // from a lot's unit, a single compensating ADD expressed in the default unit restores the
            // correct PHYSICAL quantity but leaves the raw journal net non-zero — the dish would stay
            // stuck "eaten" even though the stock is whole again. Mirroring each row's own
            // (quantity, unit) — the exact deltas already written by the eat this is undoing — makes
            // the raw net return to exactly zero, with no conversion required.
            var eatRows = stock.Journal
                .Where(j => j.SourceRef == plannedDishId && j.SourceLineRef == eatToken && j.Delta < 0m)
                .OrderBy(j => j.StockEntryId.Value)
                .ToList();
            if (eatRows.Count == 0)
            {
                addLogger.LogInformation(
                    "Eat undo skipped — eat n={EatCount} for product {ProductId} (plan dish {DishId}) removed " +
                    "nothing (full shortfall at eat time); nothing to restore.",
                    eatCount, productId, plannedDishId);
                return false;
            }

            foreach (var row in eatRows)
            {
                // Per-row token (keyed by the lot the original eat deducted from) so a racing
                // double-tapped undo dedupes each compensating row independently, the same guarantee
                // the single-row token gave before this fix.
                var rowUndoToken = Token(plannedDishId, eatCount, $"eat-undo:{row.StockEntryId.Value:N}");
                stock.AddStock(
                    -row.Delta, row.UnitId, locationId, userId, clock,
                    sourceType: StockSourceType.Eat, sourceRef: plannedDishId, sourceLineRef: rowUndoToken,
                    reason: StockReason.Correction);
            }

            await stocks.SaveChangesAsync(innerCt);

            addLogger.LogInformation(
                "Eat undone for product {ProductId} (plan dish {DishId}). {LotCount} lot(s) restored.",
                productId, plannedDishId, eatRows.Count);

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
