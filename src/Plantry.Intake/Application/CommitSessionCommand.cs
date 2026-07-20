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
///
/// <para><b>Shape.</b> <see cref="ExecuteAsync"/> is a thin phase pipeline over ports — guard/load →
/// strict-commit gate → per-line commit → finalize — mirroring the Recipes structural-health decomposition
/// (plantry-xgmb, <c>AuthorRecipe</c>). The pure per-line decisions (weight-unit resolution, price
/// observation, conversion seed) live in <see cref="LineCommitDecision"/> (plantry-tjl2.1); this class owns
/// only the IO orchestration and the lazy shared state carried across lines in a per-execution
/// <see cref="CommitContext"/> (reference data loaded at most once, store resolved at most once).</para>
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
        // ── Guard/load phase ─────────────────────────────────────────────────────────────────────────
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return Error.NotFound;
        if (session.Status != ImportStatus.Ready)
            return Error.Custom("Intake.SessionNotReady", $"Cannot commit a session in status '{session.Status}'.");

        // The stock lot's dated-as value is the (possibly user-corrected) receipt purchase date — genuine
        // backdating (plantry-yobz). Falls back to commit-time only when no date is available (a receipt with
        // no date, or one the plantry-ag05 plausibility guard nulled that the user never filled in).
        var purchasedOn = session.PurchaseDate ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // ── Strict commit gate phase (ADR-010 amendment 2026-07-11, plantry-gpdb) ──────────────────────
        if (CheckStrictCommitGate(session) is { } gateError)
            return gateError;

        // ── Per-line commit phase ──────────────────────────────────────────────────────────────────────
        // The mid-batch catch wraps ONLY this phase (guards and finalize are outside it): a genuine
        // exception becomes Intake.CommitFailed, while a line's own domain-mark failure surfaces directly.
        if (await CommitLinesAsync(session, purchasedOn, ct) is { } commitError)
            return commitError;

        // ── Finalize phase ─────────────────────────────────────────────────────────────────────────────
        return await FinalizeAsync(session, ct);
    }

    /// <summary>
    /// Strict commit gate (ADR-010 amendment 2026-07-11, plantry-gpdb). The deck-flow review surface
    /// confirms every "sure thing" up front via the explicit ConfirmLines bulk action and resolves every
    /// judgement call in the deck, so by commit time the user has already gated each line — there is no
    /// silent commit-time auto-confirm any more. Any still-<c>Pending</c> line is a genuinely unresolved
    /// line: it fails the WHOLE commit with a descriptive error naming it — never silently skipped, never
    /// half-committed. So by the time the loop runs, every line is Confirmed, Dismissed, or already-Committed.
    /// Returns the blocking <c>Intake.UnresolvedLine</c> error (and logs the warning), or null when clear.
    /// </summary>
    private Error? CheckStrictCommitGate(ImportSession session)
    {
        var unresolved = session.Lines.FirstOrDefault(l => l.Status == LineStatus.Pending);
        if (unresolved is null)
            return null;

        logger.LogWarning(
            "Import session {SessionId} commit blocked — line {LineNo} is still unresolved (Pending).",
            sessionId.Value, unresolved.LineNo);
        return Error.Custom(
            "Intake.UnresolvedLine",
            $"Line {unresolved.LineNo} (\"{unresolved.ReceiptText}\") is still unresolved and can't be added — review it first.");
    }

    /// <summary>
    /// Per-line commit phase: commit each Confirmed line in order, each in its own save (resumability,
    /// ADR-010). Non-Confirmed lines (Pending — impossible past the gate — / Dismissed / already-Committed
    /// on a retry) are skipped. Returns null on success, or the first blocking error.
    ///
    /// <para>The <c>try</c> wraps ONLY this loop. A genuine mid-batch exception (excluding cancellation) is
    /// wrapped as <c>Intake.CommitFailed</c>; a line's own <c>MarkCommitted</c> domain failure is returned
    /// directly from <see cref="CommitLineAsync"/> (as a value, never thrown) so it bypasses the catch and
    /// surfaces its true domain error code.</para>
    /// </summary>
    private async Task<Error?> CommitLinesAsync(ImportSession session, DateOnly purchasedOn, CancellationToken ct)
    {
        var context = new CommitContext(purchasedOn);
        try
        {
            foreach (var line in session.Lines)
            {
                if (line.Status != LineStatus.Confirmed)
                    continue; // skip Pending / Dismissed / already-Committed (resumability)

                if (await CommitLineAsync(session, line, context, ct) is { } lineError)
                    return lineError;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Import session {SessionId} commit failed mid-batch.",
                sessionId.Value);
            return Error.Custom("Intake.CommitFailed", ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Commits a single Confirmed line, preserving the exact side-effect ordering (ADR-010 resumability):
    /// createProduct → addStock → (ensureStore → recordPrice) → line.MarkCommitted → SaveChangesAsync →
    /// seedConversion.SeedAsync. The per-line save happens BEFORE seeding — a mid-batch failure retries
    /// cleanly without double-writing committed lines. Returns the domain error if the mark fails
    /// (surfaced directly, not via the loop's catch), else null.
    /// </summary>
    private async Task<Error?> CommitLineAsync(
        ImportSession session, ImportLine line, CommitContext context, CancellationToken ct)
    {
        var (productId, createdProductId) = await ResolveProductAsync(line, ct);

        // Stock is added in the unit the user actually committed (each-count or weight — their choice).
        var journalId = await addStock.AddStockAsync(
            productId, line.SkuId, line.Quantity!.Value, line.UnitId!.Value, line.LocationId!.Value,
            line.ExpiryDate, context.PurchasedOn, session.UserId, ct);

        // Resolve the receipt's true weight unit for a weight-priced line (plantry-1mu). Used for both the
        // price observation (never a $/each estimate) and the learned conversion anchor.
        var weightUnitId = await ResolveWeightUnitAsync(line, context, ct);

        var priceObservationId = await ObservePriceAsync(session, line, productId, weightUnitId, context, ct);

        var mark = line.MarkCommitted(journalId, priceObservationId, createdProductId);
        if (mark.IsFailure)
            return mark.Error;

        await sessions.SaveChangesAsync(ct);

        // Learn the household's weight→each factor when the user accepted an estimated each-count for an
        // existing product (plantry-1mu / plantry-x7j0 Fix A) — decided purely from the line + resolved
        // weight unit + reference lookup, and seeded AFTER the line's save. Fix A gates on the committed
        // unit's DIMENSION being Count, so a cross-weight commit (0.6 kg on an lb receipt) never seeds a
        // bogus quantity-derived factor. The conversion is tagged AiSuggested (plantry-3k44) and
        // re-derivable from the preserved weight.
        if (LineCommitDecision.DecideConversionSeed(line, weightUnitId, context.Lookup) is ConversionSeedDecision.Seed seed)
            await seedConversion.SeedAsync(productId, seed.FromUnitId, seed.ToUnitId, seed.Factor, ct);

        return null;
    }

    /// <summary>
    /// Resolves the line's product, creating it first (Catalog) when the line carries a new-product request
    /// (plantry-x7j0). Returns the resolved <c>ProductId</c> and the id of any freshly-created product (null
    /// for an existing product) so <c>MarkCommitted</c> can record the create ref.
    /// </summary>
    private async Task<(Guid ProductId, Guid? CreatedProductId)> ResolveProductAsync(ImportLine line, CancellationToken ct)
    {
        if (!line.IsNewProduct)
            return (line.ProductId!.Value, null);

        var productId = await createProduct.CreateAsync(
            line.NewProductName!, line.NewProductCategoryId!.Value, line.UnitId!.Value, ct);
        return (productId, productId);
    }

    /// <summary>
    /// Resolves the receipt's true weight unit for a line that carries a receipt weight + label (plantry-1mu).
    /// The household reference data is loaded lazily here — at most once per commit (cached on
    /// <paramref name="context"/>), and only for a line that actually carries a receipt weight + label. Returns
    /// the resolved <c>UnitId</c>, or null when the line carries no weight or its label does not resolve.
    /// </summary>
    private async Task<Guid?> ResolveWeightUnitAsync(ImportLine line, CommitContext context, CancellationToken ct)
    {
        if (line.ReceiptWeight is null || line.ReceiptWeightUnitLabel is not { } weightLabel)
            return null;

        context.Lookup ??= new ReviewReferenceLookup(await referenceData.GetAsync(ct));
        return context.Lookup.TryResolveWeightUnit(weightLabel, out var resolved) ? resolved : null;
    }

    /// <summary>
    /// Records the line's price observation per the pure decision (plantry-1mu / plantry-x7j0 Fix B): one of
    /// NoPrice (nothing to observe), SkipUnresolvedWeightUnit (a weight-carrying line whose receipt weight
    /// label did NOT resolve — recording would fall back to the committed unit and pollute pricing history
    /// with a wrong-unit $/each observation, worse than a missing one, so it is skipped and logged; stock add
    /// and conversion are unaffected), or Record (a weight-carrying line observes in the receipt's TRUE weight
    /// unit, a non-weight line in its committed quantity/unit). Returns the new observation id, or null.
    /// </summary>
    private async Task<Guid?> ObservePriceAsync(
        ImportSession session, ImportLine line, Guid productId, Guid? weightUnitId, CommitContext context, CancellationToken ct)
    {
        switch (LineCommitDecision.DecidePriceObservation(line, weightUnitId))
        {
            case PriceObservationDecision.SkipUnresolvedWeightUnit:
                logger.LogWarning(
                    "Import session {SessionId} line {LineNo}: receipt weight unit label '{WeightLabel}' did not resolve to a household unit; skipping price observation to avoid recording a wrong-unit price.",
                    sessionId.Value, line.LineNo, line.ReceiptWeightUnitLabel);
                return null;

            case PriceObservationDecision.Record record:
                var purchaseStoreId = await ResolvePurchaseStoreAsync(session, context, ct);
                return await recordPrice.RecordAsync(
                    productId, line.SkuId, record.Price, record.Quantity, record.UnitId,
                    session.MerchantText, purchaseStoreId, session.Id.Value, clock.UtcNow, session.UserId, ct);

            default: // PriceObservationDecision.NoPrice — nothing to observe.
                return null;
        }
    }

    /// <summary>
    /// Resolves the purchase → catalog.store identity (DM-16), at most once per commit and reused across the
    /// session's priced lines (cached on <paramref name="context"/>). Called only from the price-Record path,
    /// so a session with no priced lines mints no store. Two paths (plantry-yobz):
    /// <list type="number">
    /// <item>the user explicitly picked a store in review (<see cref="ImportSession.SelectedStoreId"/> set) —
    /// use that id directly, no name round-trip;</item>
    /// <item>otherwise find-or-create from <see cref="ImportSession.MerchantText"/> (covers both the
    /// untouched-AI value and a typed "create new" name). A blank/whitespace merchant maps to a null store id
    /// with NO port call (MerchantText is still retained on the observation for provenance).</item>
    /// </list>
    /// </summary>
    private async Task<Guid?> ResolvePurchaseStoreAsync(ImportSession session, CommitContext context, CancellationToken ct)
    {
        if (context.StoreResolved)
            return context.PurchaseStoreId;

        if (session.SelectedStoreId is { } pickedStoreId)
            context.PurchaseStoreId = pickedStoreId;
        else if (!string.IsNullOrWhiteSpace(session.MerchantText))
            context.PurchaseStoreId = await ensureStore.EnsureAsync(session.MerchantText, ct);
        context.StoreResolved = true;
        return context.PurchaseStoreId;
    }

    /// <summary>
    /// Finalize phase: mark the session committed (raising <c>ImportSessionCommittedEvent</c>), persist, and
    /// emit the commit telemetry counter + success log. Returns the mark failure (logged) if the session
    /// cannot transition, else success. Runs OUTSIDE the per-line catch — a finalize failure is not wrapped
    /// as <c>Intake.CommitFailed</c>.
    /// </summary>
    private async Task<Result> FinalizeAsync(ImportSession session, CancellationToken ct)
    {
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

    /// <summary>
    /// Lazy shared state carried across the per-line commit loop for a single execution (plantry-tjl2.2).
    /// Holds the once-per-commit <see cref="ReviewReferenceLookup"/> (reference data loaded lazily, only when
    /// a line needs weight-unit resolution) and the once-per-commit resolved purchase store (find-or-create on
    /// the first priced line with a non-blank merchant). This is deliberately lazy, NOT eager: a session with
    /// no weighted or priced lines issues zero reference-data / store IO.
    /// </summary>
    private sealed class CommitContext(DateOnly purchasedOn)
    {
        /// <summary>The commit date stamped on every added lot (computed once per commit).</summary>
        public DateOnly PurchasedOn { get; } = purchasedOn;

        /// <summary>The household reference lookup, loaded at most once per commit; null until first needed.</summary>
        public ReviewReferenceLookup? Lookup { get; set; }

        /// <summary>The resolved purchase store id (null for a blank merchant), valid once <see cref="StoreResolved"/> is set.</summary>
        public Guid? PurchaseStoreId { get; set; }

        /// <summary>Whether the purchase store has been resolved this commit — gates the at-most-once find-or-create.</summary>
        public bool StoreResolved { get; set; }
    }
}
