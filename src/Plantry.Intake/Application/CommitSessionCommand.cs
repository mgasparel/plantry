using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Commits a <c>Ready</c> <see cref="ImportSession"/> to the pantry (ADR-010): for each confirmed line,
/// in its own transaction, create the product if it is new (Catalog), record the purchased lot
/// (Inventory, <c>source = Intake</c>), and record the purchase price (Pricing) — then mark the line
/// committed with those refs. Finally the session itself is marked committed (raising
/// <c>ImportSessionCommittedEvent</c>).
///
/// <para><b>Strict commit gate (ADR-010 amendment 2026-07-11, plantry-gpdb).</b> Commit requires that no
/// line is still <c>Pending</c>. The deck-flow review surface confirms every "sure thing" up front via the
/// explicit <see cref="ConfirmLinesCommand"/> bulk action and resolves every judgement call in the deck, so
/// the user has already gated each high-confidence line — there is no longer a silent commit-time
/// auto-confirm pre-pass. Any still-<c>Pending</c> line is therefore a genuinely unresolved line: it fails
/// the <em>whole</em> commit with a descriptive error naming it — never silently skipped, never
/// half-committed. So by the time the loop runs, every line is Confirmed, Dismissed, or already-Committed.</para>
///
/// <para><b>Resumable.</b> Each line is saved with its committed refs before the next line runs, and lines
/// that are not Confirmed at loop time (Dismissed, or already-Committed on a retry) are skipped — so a
/// mid-batch failure can be retried and re-runs cleanly without double-writing committed lines. (A failure
/// <em>within</em> a single line — e.g. after stock but before price — is not transactional across contexts;
/// that line re-runs on retry. Acceptable for Phase 1; revisit with an outbox if it bites.)</para>
/// </summary>
public sealed class CommitSessionCommand(
    ImportSessionId sessionId,
    IImportSessionRepository sessions,
    ICreateProductPort createProduct,
    IAddStockPort addStock,
    IRecordPricePort recordPrice,
    IEnsurePurchaseStorePort ensureStore,
    IReviewReferenceDataProvider referenceData,
    ISeedConversionPort seedConversion,
    IClock clock,
    ITenantContext tenant,
    ILogger<CommitSessionCommand> logger)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return Error.NotFound;
        if (session.Status != ImportStatus.Ready)
            return Error.Custom("Intake.SessionNotReady", $"Cannot commit a session in status '{session.Status}'.");

        var purchasedOn = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // Weight→each support (plantry-1mu / plantry-x7j0): the household's Catalog reference data is
        // fetched at most once per commit and cached here, then two lookups are derived from it lazily —
        // a weight-unit-label→UnitId map (so a weight-priced line's price observation stays in the
        // receipt's TRUE unit) and a UnitId→dimension map (so a conversion is only ever seeded when the
        // committed unit is Count, never a bogus weight→weight factor). Loaded only if a line needs it.
        ReviewReferenceData? reference = null;
        IReadOnlyDictionary<string, Guid>? unitIdByLabel = null;
        IReadOnlyDictionary<Guid, ReviewUnitDimension>? dimensionByUnitId = null;

        // Merchant → catalog.store identity (DM-16), resolved find-or-create at most once per commit and
        // reused across the session's priced lines. Blank merchant → null store_id (unchanged); MerchantText
        // is retained on the observation for provenance. Resolved lazily on the first priced line so a
        // session with no priced lines mints no store.
        Guid? purchaseStoreId = null;
        var storeResolved = false;

        // ── Strict commit gate (ADR-010 amendment 2026-07-11, plantry-gpdb) ──────────────────────────
        // The deck-flow review surface confirms every "sure thing" up front via the explicit ConfirmLines
        // bulk action and resolves every judgement call in the deck, so by commit time the user has already
        // gated each line — there is no silent commit-time auto-confirm any more. Any still-Pending line is a
        // genuinely unresolved line: it fails the WHOLE commit with a descriptive error naming it — never
        // silently skipped, never half-committed. So by the time the loop runs, every line is Confirmed,
        // Dismissed, or already-Committed.
        var unresolved = session.Lines.FirstOrDefault(l => l.Status == LineStatus.Pending);
        if (unresolved is not null)
        {
            logger.LogWarning(
                "Import session {SessionId} commit blocked — line {LineNo} is still unresolved (Pending).",
                sessionId.Value, unresolved.LineNo);
            return Error.Custom(
                "Intake.UnresolvedLine",
                $"Line {unresolved.LineNo} (\"{unresolved.ReceiptText}\") is still unresolved and can't be added — review it first.");
        }

        try
        {
            foreach (var line in session.Lines)
            {
                if (line.Status != LineStatus.Confirmed)
                    continue; // skip Pending / Dismissed / already-Committed (resumability)

                Guid productId;
                Guid? createdProductId = null;
                if (line.IsNewProduct)
                {
                    productId = await createProduct.CreateAsync(
                        line.NewProductName!, line.NewProductCategoryId!.Value, line.UnitId!.Value, ct);
                    createdProductId = productId;
                }
                else
                {
                    productId = line.ProductId!.Value;
                }

                // Stock is added in the unit the user actually committed (each-count or weight — their choice).
                var journalId = await addStock.AddStockAsync(
                    productId, line.SkuId, line.Quantity!.Value, line.UnitId!.Value, line.LocationId!.Value,
                    line.ExpiryDate, purchasedOn, session.UserId, ct);

                // Resolve the receipt's true weight unit for a weight-priced line (plantry-1mu). Used for
                // both the price observation (never a $/each estimate) and the learned conversion anchor.
                Guid? weightUnitId = null;
                if (line.ReceiptWeight is not null && line.ReceiptWeightUnitLabel is { } weightLabel)
                {
                    reference ??= await referenceData.GetAsync(ct);
                    unitIdByLabel ??= BuildUnitLabelMap(reference);
                    if (unitIdByLabel.TryGetValue(weightLabel, out var resolved))
                        weightUnitId = resolved;
                }

                Guid? priceObservationId = null;
                if (line.Price is { } price)
                {
                    // Fix B (plantry-x7j0): a weight-carrying line whose receipt weight label could NOT be
                    // resolved to a household unit has no true unit to observe in. Recording it would fall
                    // back to the committed unit — and if the user accepted the each-count that is a $/each
                    // observation, exactly what plantry-1mu forbids. A wrong-unit price is worse than a
                    // missing one, so skip the observation entirely (stock add and conversion are unaffected)
                    // and log the unresolvable label.
                    if (line.ReceiptWeight is not null && weightUnitId is null)
                    {
                        logger.LogWarning(
                            "Import session {SessionId} line {LineNo}: receipt weight unit label '{WeightLabel}' did not resolve to a household unit; skipping price observation to avoid recording a wrong-unit price.",
                            sessionId.Value, line.LineNo, line.ReceiptWeightUnitLabel);
                    }
                    else
                    {
                        if (!storeResolved)
                        {
                            if (!string.IsNullOrWhiteSpace(session.MerchantText))
                                purchaseStoreId = await ensureStore.EnsureAsync(session.MerchantText, ct);
                            storeResolved = true;
                        }

                        // Pricing observes in the receipt's TRUE unit: when the line carries a receipt weight,
                        // record the weight + resolved weight unit regardless of what unit the stock committed
                        // in, so an accepted each-count never pollutes pricing history with a $/each
                        // observation (plantry-1mu). weightUnitId is guaranteed non-null here for a
                        // weight-carrying line — the unresolved case was skipped above.
                        var (priceQty, priceUnitId) = line.ReceiptWeight is { } w
                            ? (w, weightUnitId!.Value)
                            : (line.Quantity!.Value, line.UnitId!.Value);

                        priceObservationId = await recordPrice.RecordAsync(
                            productId, line.SkuId, price, priceQty, priceUnitId,
                            session.MerchantText, purchaseStoreId, session.Id.Value, clock.UtcNow, session.UserId, ct);
                    }
                }

                var mark = line.MarkCommitted(journalId, priceObservationId, createdProductId);
                if (mark.IsFailure)
                    return mark.Error;

                await sessions.SaveChangesAsync(ct);

                // Learn the household's weight→each factor when the user accepted an estimated each-count
                // for an existing product. Fix A (plantry-x7j0): gate on the committed unit's DIMENSION being
                // Count — not merely "differs from the receipt weight unit". Committing a weight-priced line
                // in a *different weight* unit (e.g. 0.6 kg on an lb receipt) must NOT seed a quantity-derived
                // "lb→kg" factor: cross-weight conversion is a fixed physical constant, never receipt-derived.
                // The conversion is tagged AiSuggested (plantry-3k44) and re-derivable from the preserved weight.
                if (!line.IsNewProduct
                    && line.HasEachEstimate
                    && weightUnitId is { } fromUnit
                    && (dimensionByUnitId ??= BuildDimensionMap(reference!)).TryGetValue(line.UnitId!.Value, out var committedDimension)
                    && committedDimension == ReviewUnitDimension.Count
                    && line.ReceiptWeight is { } receiptWeight && receiptWeight > 0m)
                {
                    var factor = line.Quantity!.Value / receiptWeight; // each per weight unit
                    await seedConversion.SeedAsync(productId, fromUnit, line.UnitId!.Value, factor, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Import session {SessionId} commit failed mid-batch.",
                sessionId.Value);
            return Error.Custom("Intake.CommitFailed", ex.Message);
        }

        var sessionMark = session.MarkCommitted(clock.UtcNow);
        if (sessionMark.IsFailure)
        {
            logger.LogWarning(
                "Import session {SessionId} could not be marked committed: {ErrorCode}.",
                sessionId.Value, sessionMark.Error.Code);
            return sessionMark.Error;
        }

        await sessions.SaveChangesAsync(ct);

        DomainTelemetry.IntakeSessionsCommitted.Add(1);
        logger.LogInformation(
            "Import session {SessionId} committed successfully.",
            sessionId.Value);

        return Result.Success();
    }

    /// <summary>Builds a case-insensitive weight-unit-label → catalog UnitId map from already-fetched
    /// reference data, so a receipt weight label ("lb"/"kg") resolves to the unit the price observation is
    /// recorded in.</summary>
    private static IReadOnlyDictionary<string, Guid> BuildUnitLabelMap(ReviewReferenceData reference)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in reference.Units)
            map[unit.Code] = unit.Id; // last-wins; unit codes are unique per household
        return map;
    }

    /// <summary>Builds a UnitId → dimension map from already-fetched reference data, so the conversion-seed
    /// gate can require the committed unit be Count before learning a weight→each factor (plantry-x7j0).</summary>
    private static IReadOnlyDictionary<Guid, ReviewUnitDimension> BuildDimensionMap(ReviewReferenceData reference)
    {
        var map = new Dictionary<Guid, ReviewUnitDimension>();
        foreach (var unit in reference.Units)
            map[unit.Id] = unit.Dimension; // last-wins; unit ids are unique per household
        return map;
    }
}
