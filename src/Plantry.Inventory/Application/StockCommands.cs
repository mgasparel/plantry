using Microsoft.Extensions.Logging;
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
    StockSourceType sourceType = StockSourceType.Manual,
    ILogger<AddStockCommand>? logger = null,
    Guid? sourceRef = null,
    Guid? sourceLineRef = null)
{
    public async Task<Result<StockEntryId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (quantity <= 0m)
        {
            logger?.LogWarning(
                "AddStock rejected — invalid quantity {Quantity} for product {ProductId}.",
                quantity, productId);
            return Error.Custom("Inventory.InvalidQuantity", "Quantity must be greater than zero.");
        }

        var product = await catalog.FindProductAsync(productId, ct);
        if (product is null)
        {
            logger?.LogWarning("AddStock failed — product {ProductId} not found.", productId);
            return Error.Custom("Inventory.UnknownProduct", "The selected product does not exist.");
        }
        if (!product.CanHoldStock)
        {
            logger?.LogWarning(
                "AddStock failed — product {ProductId} cannot hold stock directly (is a parent).", productId);
            return Error.Custom("Inventory.ProductCannotHoldStock", "A parent product cannot hold stock directly; choose a variant.");
        }

        var household = HouseholdId.From(householdId);
        // When an idempotency token is supplied (yield-on-cook produce, plantry-854a) the journal must be
        // loaded so AddStock can short-circuit a re-driven produce; FindWithHistoryAsync brings it in.
        // Manual / intake adds pass no token and stay on the cheaper Entries-only FindAsync.
        var stock = sourceLineRef is null
            ? await stocks.FindAsync(household, productId, ct)
            : await stocks.FindWithHistoryAsync(household, productId, ct);
        var isNew = stock is null;
        stock ??= ProductStock.Start(household, productId, clock);

        var entry = stock.AddStock(
            quantity, unitId, locationId, userId, clock,
            skuId: skuId, expiryDate: expiryDate, purchasedAt: purchasedAt,
            sourceType: sourceType, sourceRef: sourceRef, sourceLineRef: sourceLineRef);

        if (isNew)
        {
            if (!await stocks.TryAddAndSaveAsync(stock, ct))
            {
                // Concurrent first-intake race: another request won the root insert.
                // Reload and re-add the lot to the existing root.
                stock = (sourceLineRef is null
                    ? await stocks.FindAsync(household, productId, ct)
                    : await stocks.FindWithHistoryAsync(household, productId, ct))!;
                entry = stock.AddStock(
                    quantity, unitId, locationId, userId, clock,
                    skuId: skuId, expiryDate: expiryDate, purchasedAt: purchasedAt,
                    sourceType: sourceType, sourceRef: sourceRef, sourceLineRef: sourceLineRef);
                await stocks.SaveChangesAsync(ct);
            }
        }
        else
        {
            await stocks.SaveChangesAsync(ct);
        }

        logger?.LogInformation(
            "Stock added for product {ProductId}. Quantity: {Quantity}, SourceType: {SourceType}, EntryId: {EntryId}.",
            productId, quantity, sourceType, entry.Id.Value);

        return entry.Id;
    }
}

/// <summary>
/// Sets or clears the per-household, per-product low stock threshold and persists the change.
/// Mirrors the load-or-start pattern from <see cref="AddStockCommand"/>: a household can set a
/// threshold before any lot exists, so a missing root is started fresh and saved via
/// <see cref="IProductStockRepository.TryAddAndSaveAsync"/>; if the insert races, reload and apply again.
/// </summary>
public sealed class SetLowStockThresholdCommand(
    Guid productId,
    decimal? threshold,
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IClock clock,
    ITenantContext tenant,
    ILogger<SetLowStockThresholdCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var product = await catalog.FindProductAsync(productId, ct);
        if (product is null)
        {
            logger?.LogWarning("SetLowStockThreshold failed — product {ProductId} not found.", productId);
            return Error.Custom("Inventory.UnknownProduct", "The selected product does not exist.");
        }
        if (!product.CanHoldStock)
        {
            logger?.LogWarning(
                "SetLowStockThreshold failed — product {ProductId} cannot hold stock directly (is a parent).", productId);
            return Error.Custom("Inventory.ProductCannotHoldStock", "A parent product cannot hold stock directly; choose a variant.");
        }

        var household = HouseholdId.From(householdId);
        var stock = await stocks.FindAsync(household, productId, ct);
        var isNew = stock is null;
        stock ??= ProductStock.Start(household, productId, clock);

        stock.SetLowStockThreshold(threshold, clock);

        if (isNew)
        {
            if (!await stocks.TryAddAndSaveAsync(stock, ct))
            {
                // Concurrent first-intake race: another request won the root insert.
                // Reload and re-apply the threshold to the existing root.
                stock = (await stocks.FindAsync(household, productId, ct))!;
                stock.SetLowStockThreshold(threshold, clock);
                await stocks.SaveChangesAsync(ct);
            }
        }
        else
        {
            await stocks.SaveChangesAsync(ct);
        }

        logger?.LogInformation(
            "Low-stock threshold set for product {ProductId}. Threshold: {Threshold}.",
            productId, threshold);

        return Result.Success();
    }
}

/// <summary>
/// The single consumption entry point for the UI (SPEC §1c/§1d) — used (<c>Consumed</c>), wasted
/// (<c>Discarded</c>), or manually corrected (<c>Correction</c>). Resolves the product's converter
/// through the <see cref="IProductConversionProvider"/> port, loads the root under a row lock, and
/// runs <see cref="ProductStock.Consume"/> inside a transaction so the lock serializes concurrent
/// consumes (DM-13). Returns the <see cref="ConsumeOutcome"/> so the UI can surface any shortfall.
///
/// The post-save low-stock check converts active lots to the product's display unit (via
/// <see cref="ICatalogReadFacade.FindProductAsync"/> + <see cref="IProductConversionProvider"/>)
/// before calling <see cref="ProductStock.IsRunningLow"/>, mirroring the pantry-list read path.
///
/// <paramref name="sourceLineRef"/> is the per-consume-operation idempotency token (plantry-292a).
/// When supplied, a re-driven consume with the same token is a no-op — the repository row-lock plus
/// the aggregate's journal scan guarantee this is safe to call multiple times from the cook adapter.
/// Pass <c>null</c> from the manual-consume path (Pantry Detail) to leave existing behaviour unchanged.
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
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant,
    StockSourceType sourceType = StockSourceType.Manual,
    Guid? sourceLineRef = null,
    ILogger<ConsumeStockCommand>? logger = null)
{
    public async Task<Result<ConsumeOutcome>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (!reason.IsRemoval())
            return Error.Custom("Inventory.InvalidConsumeReason", "Consume cannot record a Purchase; use AddStock.");

        // Resolve the product's default display unit (for the low-stock check below) and the
        // converter before entering the row-lock transaction, so the catalog read does not run
        // under the Inventory row lock.
        var product = await catalog.FindProductAsync(productId, ct);
        var converter = await conversions.ForProductAsync(productId, ct);
        var household = HouseholdId.From(householdId);

        return await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);
            if (stock is null)
            {
                logger?.LogWarning(
                    "Consume failed — no stock record for product {ProductId}. Reason: {Reason}, SourceType: {SourceType}.",
                    productId, reason, sourceType);
                return Result<ConsumeOutcome>.Failure(Error.Custom("Inventory.NoStock", "There is no stock for this product."));
            }

            var outcome = stock.Consume(
                amount, unitId, reason, converter, userId, clock,
                sourceRef: sourceRef,
                sourceType: sourceType,
                targetEntry: targetEntryId is { } id ? StockEntryId.From(id) : null,
                sourceLineRef: sourceLineRef,
                dueDaysAfterOpening: product?.DefaultDueDaysAfterOpening);

            if (outcome.IsFailure)
            {
                logger?.LogWarning(
                    "Consume planning pass failed for product {ProductId}. Reason: {Reason}, SourceType: {SourceType}, Error: {ErrorCode}.",
                    productId, reason, sourceType, outcome.Error.Code);
                return outcome; // nothing mutated (planning pass failed) — commits as a no-op
            }

            await stocks.SaveChangesAsync(innerCt);

            DomainTelemetry.StockConsumed.Add(1);

            // Emit a low-stock event when the consume drops on-hand to or below the threshold.
            // Mirrors the InventoryQueryService read path (DisplayQuantity): convert active lots
            // to the product's display unit via IProductConversionProvider before calling
            // IsRunningLow. Falls back to a raw sum when (a) the product is unknown or (b)
            // conversion yields zero for a non-empty lot set (incompatible units — e.g. "ea"
            // lots on a "g" product), mirroring DisplayQuantity's own incompatible-unit fallback
            // so the counter always agrees with the displayed on-hand state.
            // Uses the shared InventoryQueryService.SumInDisplayUnit helper (both paths must agree).
            var activeLots = stock.Entries.Where(e => e.IsActive).ToList();
            decimal onHand;
            if (product is not null)
            {
                onHand = InventoryQueryService.SumInDisplayUnit(activeLots, product.DefaultUnitId, converter);
                if (onHand == 0m && activeLots.Count > 0)
                    onHand = activeLots.Sum(e => e.Quantity); // conversion failed entirely — mirror DisplayQuantity's fallback
            }
            else
            {
                onHand = activeLots.Sum(e => e.Quantity);
            }
            if (stock.IsRunningLow(onHand))
                DomainTelemetry.LowStockEvents.Add(1);

            logger?.LogInformation(
                "Stock consumed for product {ProductId}. Reason: {Reason}, SourceType: {SourceType}, Amount: {Amount}.",
                productId, reason, sourceType, amount);

            return outcome;
        }, ct);
    }
}

/// <summary>
/// The Inventory leg of purchase-entry amendment (ADR-023, <c>docs/DomainDesign/purchase-entry-amendment.md</c>,
/// origin plantry-x3dy) — fixes the committed quantity of a mis-entered purchase without breaking
/// the append-only ledger. Loads the root under the same row lock as <see cref="ConsumeStockCommand"/>
/// (DM-13) and delegates the guards and journal-append mechanics to
/// <see cref="ProductStock.AmendPurchase"/>. This command is the Inventory-side collaborator that
/// <c>AmendCommittedLineCommand</c> in <c>Plantry.Intake.Application</c> orchestrates alongside the
/// pricing supersede step (ADR-014 — no shared transaction, so each side commits independently and
/// the whole sequence is safe to re-drive on partial failure).
/// </summary>
public sealed class AmendPurchaseCommand(
    Guid productId,
    Guid stockEntryId,
    decimal correctedQuantity,
    Guid importLineId,
    Guid userId,
    IProductStockRepository stocks,
    IClock clock,
    ITenantContext tenant,
    ILogger<AmendPurchaseCommand>? logger = null)
{
    public async Task<Result<decimal>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var household = HouseholdId.From(householdId);

        return await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);
            if (stock is null)
            {
                logger?.LogWarning("AmendPurchase failed — no stock record for product {ProductId}.", productId);
                return Result<decimal>.Failure(Error.Custom("Inventory.NoStock", "There is no stock for this product."));
            }

            var result = stock.AmendPurchase(
                StockEntryId.From(stockEntryId), correctedQuantity, importLineId, userId, clock);

            if (result.IsFailure)
            {
                logger?.LogWarning(
                    "AmendPurchase rejected for product {ProductId}, entry {EntryId}. Error: {ErrorCode}.",
                    productId, stockEntryId, result.Error.Code);
                return result;
            }

            await stocks.SaveChangesAsync(innerCt);

            logger?.LogInformation(
                "Purchase amended for product {ProductId}, entry {EntryId}. Delta: {Delta}.",
                productId, stockEntryId, result.Value);

            return result;
        }, ct);
    }
}

/// <summary>
/// The "Mark opened" row action (plantry-1le6, UI spec §1) — a one-tap, no-input command. Resolves
/// the product's after-opening default through <see cref="ICatalogReadFacade"/> (Inventory must not
/// reach into Catalog directly) and delegates the clamp/guards to <see cref="ProductStock.MarkOpened"/>.
/// Loads the root under the same row lock as <see cref="ConsumeStockCommand"/> (DM-13) even though this
/// is not consumption (rule 6: no journal row, no quantity change) — <c>IsOpen</c>/<c>ExpiryDate</c> are
/// still fields on the same concurrency-anchored aggregate.
/// </summary>
public sealed class MarkStockOpenedCommand(
    Guid productId,
    Guid stockEntryId,
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IClock clock,
    ITenantContext tenant,
    ILogger<MarkStockOpenedCommand>? logger = null)
{
    public async Task<Result<MarkOpenedOutcome>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var household = HouseholdId.From(householdId);
        var product = await catalog.FindProductAsync(productId, ct);

        return await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);
            if (stock is null)
            {
                logger?.LogWarning("MarkOpened failed — no stock record for product {ProductId}.", productId);
                return Result<MarkOpenedOutcome>.Failure(Error.Custom("Inventory.NoStock", "There is no stock for this product."));
            }

            var result = stock.MarkOpened(StockEntryId.From(stockEntryId), product?.DefaultDueDaysAfterOpening, clock);
            if (result.IsFailure)
            {
                logger?.LogWarning(
                    "MarkOpened rejected for product {ProductId}, entry {EntryId}. Error: {ErrorCode}.",
                    productId, stockEntryId, result.Error.Code);
                return result;
            }

            await stocks.SaveChangesAsync(innerCt);

            logger?.LogInformation(
                "Lot marked opened for product {ProductId}, entry {EntryId}.", productId, stockEntryId);

            return result;
        }, ct);
    }
}

/// <summary>
/// The "Open" badge's tap-to-undo action (plantry-1le6, UI spec §3) — corrections happen; un-marking
/// does NOT restore the expiry opening replaced (no history is kept) and runs no recompute. Same row
/// lock as <see cref="MarkStockOpenedCommand"/>; needs no Catalog fact at all.
/// </summary>
public sealed class UnmarkStockOpenedCommand(
    Guid productId,
    Guid stockEntryId,
    IProductStockRepository stocks,
    IClock clock,
    ITenantContext tenant,
    ILogger<UnmarkStockOpenedCommand>? logger = null)
{
    public async Task<Result<UnmarkOpenedOutcome>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var household = HouseholdId.From(householdId);

        return await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);
            if (stock is null)
            {
                logger?.LogWarning("UnmarkOpened failed — no stock record for product {ProductId}.", productId);
                return Result<UnmarkOpenedOutcome>.Failure(Error.Custom("Inventory.NoStock", "There is no stock for this product."));
            }

            var result = stock.UnmarkOpened(StockEntryId.From(stockEntryId), clock);
            if (result.IsFailure)
            {
                logger?.LogWarning(
                    "UnmarkOpened rejected for product {ProductId}, entry {EntryId}. Error: {ErrorCode}.",
                    productId, stockEntryId, result.Error.Code);
                return result;
            }

            await stocks.SaveChangesAsync(innerCt);

            logger?.LogInformation(
                "Lot unmarked opened for product {ProductId}, entry {EntryId}.", productId, stockEntryId);

            return result;
        }, ct);
    }
}

/// <summary>
/// The "Move" action (plantry-6owm) — inventory transfer between locations, with the freeze/thaw
/// expiry recompute implicit in the destination's <c>LocationType</c> (rule 2). Resolves the
/// destination's (and the lot's current) frozen-ness plus the product's after-freezing/after-thawing
/// defaults through <see cref="ICatalogReadFacade"/> (Inventory must not reach into Catalog directly)
/// and delegates the transition math/guards/split mechanics to <see cref="ProductStock.Transfer"/>.
/// Loads the root under the same row lock as <see cref="ConsumeStockCommand"/> (DM-13) even though
/// this is not consumption (rule 7: no journal row, no quantity-consumed accounting change).
/// </summary>
public sealed class TransferStockCommand(
    Guid productId,
    Guid stockEntryId,
    Guid destinationLocationId,
    decimal quantity,
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IClock clock,
    ITenantContext tenant,
    ILogger<TransferStockCommand>? logger = null)
{
    public async Task<Result<TransferOutcome>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var product = await catalog.FindProductAsync(productId, ct);
        var frozenFlags = await catalog.GetLocationFrozenFlagsAsync(ct);
        if (!frozenFlags.TryGetValue(destinationLocationId, out var destinationIsFrozen))
        {
            logger?.LogWarning("Transfer failed — destination location {LocationId} not found.", destinationLocationId);
            return Error.Custom("Inventory.UnknownLocation", "The selected destination location does not exist.");
        }

        var household = HouseholdId.From(householdId);
        var entry = StockEntryId.From(stockEntryId);

        return await stocks.ExecuteInTransactionAsync(async innerCt =>
        {
            var stock = await stocks.FindForUpdateAsync(household, productId, innerCt);
            if (stock is null)
            {
                logger?.LogWarning("Transfer failed — no stock record for product {ProductId}.", productId);
                return Result<TransferOutcome>.Failure(Error.Custom("Inventory.NoStock", "There is no stock for this product."));
            }

            var lot = stock.Entries.FirstOrDefault(e => e.Id == entry);
            if (lot is null)
            {
                logger?.LogWarning("Transfer failed — no lot {EntryId} for product {ProductId}.", stockEntryId, productId);
                return Result<TransferOutcome>.Failure(Error.Custom("Inventory.LotNotFound", $"No lot '{stockEntryId}' to move."));
            }

            // Defensive: an unknown/archived source location (should not happen for a live lot) is
            // treated as non-frozen rather than failing the whole transfer.
            var sourceIsFrozen = frozenFlags.GetValueOrDefault(lot.LocationId);

            var result = stock.Transfer(
                entry, destinationLocationId, sourceIsFrozen, destinationIsFrozen, quantity, clock,
                product?.DefaultDueDaysAfterFreezing, product?.DefaultDueDaysAfterThawing);

            if (result.IsFailure)
            {
                logger?.LogWarning(
                    "Transfer rejected for product {ProductId}, entry {EntryId}. Error: {ErrorCode}.",
                    productId, stockEntryId, result.Error.Code);
                return result;
            }

            await stocks.SaveChangesAsync(innerCt);

            logger?.LogInformation(
                "Lot moved for product {ProductId}, entry {EntryId} → location {DestinationLocationId}. Kind: {Kind}.",
                productId, stockEntryId, destinationLocationId, result.Value.Kind);

            return result;
        }, ct);
    }
}
