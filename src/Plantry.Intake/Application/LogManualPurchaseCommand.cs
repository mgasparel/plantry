using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// One line of a typed manual purchase (plantry-45ba.2): resolves to either an existing Catalog product
/// (<see cref="ProductId"/>) or a brand-new one (<see cref="NewProductName"/> + <see cref="NewProductCategoryId"/>),
/// never both, never neither — enforced only here: the domain's <see cref="ImportLine.Confirm"/> /
/// <see cref="ImportLine.ConfirmAsNew"/> guard line status and a blank new-product name, but neither guards the
/// either/or product choice, the quantity, or the price. Checked up front so the whole submission fails
/// atomically before any line is added to the session. <see cref="Price"/> is optional — a blank price commits
/// stock only (<see cref="LineCommitDecision.DecidePriceObservation"/> already returns <c>NoPrice</c>).
/// </summary>
public sealed record ManualPurchaseLineInput(
    Guid? ProductId,
    string? NewProductName,
    Guid? NewProductCategoryId,
    decimal Quantity,
    Guid UnitId,
    Guid LocationId,
    decimal? Price = null,
    DateOnly? ExpiryDate = null);

/// <summary>
/// Commits a typed purchase (store, date, one or more lines) in a single call — the application command
/// behind the manual-intake form (parent design plantry-45ba). Reuses the existing receipt-intake domain
/// and commit path end-to-end so a manual purchase is indistinguishable, once committed, from a receipt scan:
/// same stock lots (<c>StockSourceType.Intake</c>), same price observations (<c>PriceSource.Purchase</c>),
/// same intake history and pantry provenance.
///
/// <para><b>Sequence</b> (no new <see cref="ImportSession"/> members needed): <see cref="ImportSession.Start"/>
/// with <see cref="ImportSourceType.Manual"/> (→ Parsing) → <see cref="ImportSession.MarkReady"/> with the
/// typed purchase date as receipt metadata (→ Ready) → <see cref="ImportSession.CorrectHeader"/> to carry an
/// explicitly-picked store id straight through to <see cref="CommitSessionCommand.ResolvePurchaseStoreAsync"/>
/// → one <see cref="ImportSession.AddLine"/> + <see cref="ImportLine.Confirm"/>/<see cref="ImportLine.ConfirmAsNew"/>
/// per typed line, every <c>Suggested*</c> field left null (there is no AI suggestion on a typed line — the
/// line's own <see cref="SuggestedConfidence"/> is <see cref="SuggestedConfidence.None"/>, literally true) →
/// save → delegate to the unchanged <see cref="CommitSessionCommand"/>.</para>
///
/// <para><b>Line label.</b> <see cref="ImportLine.ReceiptText"/> is the user-facing label for the line in
/// history and provenance; for a manual line it is the resolved product name (looked up from the household's
/// reference data for an existing product) or the typed new-product name.</para>
///
/// <para><b>Referential ids.</b> <see cref="ManualPurchaseLineInput.UnitId"/>, <see cref="ManualPurchaseLineInput.LocationId"/>,
/// and <see cref="ManualPurchaseLineInput.NewProductCategoryId"/> are trusted the same way
/// <see cref="ResolveLineCommand"/> and <see cref="ConfirmLineAsNewCommand"/> already trust them for the receipt
/// review flow — the downstream write ports (<see cref="IAddStockPort"/>, <see cref="ICreateProductPort"/>,
/// <see cref="IRecordPricePort"/>) are themselves household-scoped (Gate 3) and reject a cross-tenant or unknown
/// id when the line actually commits. <see cref="ManualPurchaseLineInput.ProductId"/> and
/// <see cref="SelectedStoreId"/> are the two exceptions: both are validated against the household's own
/// reference data here (mirroring <see cref="CorrectSessionHeaderCommand"/>'s store check exactly), because —
/// unlike a rejected downstream write — an unknown id in either spot would otherwise commit successfully with
/// a wrong or garbage label (a bogus "Unknown product" <see cref="ImportLine.ReceiptText"/>, or a misattributed
/// store) rather than simply failing.</para>
///
/// <para><b>Failure semantics.</b> A single submit, so a mid-commit failure leaves a <c>Ready</c> session with
/// some lines already committed — resumable by <see cref="CommitSessionCommand"/>'s own design, but with no
/// manual resume surface (accepted per the parent design). The session stays visible in Intake history either
/// way; <see cref="ExecuteAsync"/> enriches <see cref="CommitSessionCommand"/>'s error with how many of the
/// typed lines actually committed before the failure, rather than surfacing a bare "commit failed".</para>
/// </summary>
public sealed class LogManualPurchaseCommand(
    Guid userId,
    string? merchantText,
    Guid? selectedStoreId,
    DateOnly purchaseDate,
    IReadOnlyList<ManualPurchaseLineInput> lines,
    IImportSessionRepository sessions,
    ICreateProductPort createProduct,
    IAddStockPort addStock,
    IRecordPricePort recordPrice,
    IEnsurePurchaseStorePort ensureStore,
    IReviewReferenceDataProvider referenceData,
    ISeedConversionPort seedConversion,
    IClock clock,
    ITenantContext tenant,
    ILogger<CommitSessionCommand> commitLogger,
    ILogger<LogManualPurchaseCommand>? logger = null)
{
    public async Task<Result<ImportSessionId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (ValidateLines() is { } validationError)
        {
            logger?.LogWarning("LogManualPurchase rejected — {ErrorCode}.", validationError.Code);
            return validationError;
        }

        var reference = await referenceData.GetAsync(ct);

        if (selectedStoreId is { } storeId && reference.Stores.All(s => s.Id != storeId))
        {
            logger?.LogWarning(
                "LogManualPurchase rejected — store {StoreId} is not an active store for household {HouseholdId}.",
                storeId, householdId);
            return Error.Custom("Intake.UnknownStore", "The selected store no longer exists — pick another or create a new one.");
        }

        var productNames = reference.Products.ToDictionary(p => p.Id, p => p.Name);

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].ProductId is { } pid && !productNames.ContainsKey(pid))
            {
                logger?.LogWarning(
                    "LogManualPurchase rejected — product {ProductId} is not a stockable product for household {HouseholdId}.",
                    pid, householdId);
                return Error.Custom(
                    "Intake.UnknownProduct",
                    $"Line {i + 1}: that product no longer exists — pick another or add it as a new product.");
            }
        }

        var household = HouseholdId.From(householdId);
        var session = ImportSession.Start(household, ImportSourceType.Manual, userId, clock);
        await sessions.AddAsync(session, ct);

        session.MarkReady(merchantText, clock.UtcNow, new ReceiptMetadata(PurchaseDate: purchaseDate));
        var header = session.CorrectHeader(merchantText, selectedStoreId, purchaseDate, purchaseTime: null, clock);
        if (header.IsFailure)
        {
            logger?.LogWarning(
                "LogManualPurchase failed to set the session header for {SessionId}: {ErrorCode}.",
                session.Id.Value, header.Error.Code);
            return header.Error;
        }

        var lineNo = 1;
        foreach (var input in lines)
        {
            var isNewProduct = input.ProductId is null;
            var label = isNewProduct ? input.NewProductName! : productNames[input.ProductId!.Value];

            var line = session.AddLine(lineNo, label, SuggestedConfidence.None, rawPayload: null);
            var confirm = isNewProduct
                ? line.ConfirmAsNew(
                    input.NewProductName!, input.NewProductCategoryId!.Value, input.Quantity, input.UnitId,
                    input.LocationId, input.ExpiryDate, input.Price)
                : line.Confirm(
                    input.ProductId!.Value, skuId: null, input.Quantity, input.UnitId, input.LocationId,
                    input.ExpiryDate, input.Price);

            if (confirm.IsFailure)
            {
                logger?.LogWarning(
                    "LogManualPurchase failed to confirm line {LineNo} for session {SessionId}: {ErrorCode}.",
                    lineNo, session.Id.Value, confirm.Error.Code);
                return confirm.Error;
            }

            lineNo++;
        }

        await sessions.SaveChangesAsync(ct);

        var commitResult = await new CommitSessionCommand(
            session.Id, sessions, createProduct, addStock, recordPrice, ensureStore,
            referenceData, seedConversion, clock, tenant, commitLogger).ExecuteAsync(ct);

        if (commitResult.IsFailure)
        {
            logger?.LogWarning(
                "Manual purchase commit failed for import session {SessionId}: {ErrorCode} ({CommittedLines} of {TotalLines} line(s) committed).",
                session.Id.Value, commitResult.Error.Code,
                session.Lines.Count(l => l.Status == LineStatus.Committed), session.Lines.Count);
            return DescribePartialCommit(session, commitResult.Error);
        }

        logger?.LogInformation(
            "Manual purchase logged — import session {SessionId} committed with {LineCount} line(s) (store picked: {StorePicked}).",
            session.Id.Value, lines.Count, selectedStoreId is not null);

        return session.Id;
    }

    /// <summary>
    /// Validates the typed-purchase invariants up front, atomically — before a single line is added to
    /// the session — so a bad submission fails whole, with no half-built session: a real purchase date
    /// (not the unset <c>default</c> a page-level guard failing to run would let through — the web layer
    /// has its own guard too, but this is the load-bearing one for any caller); at least one line;
    /// quantity &gt; 0; a supplied price &gt;= 0; and each line resolves to either an existing product or a
    /// new-product request, never both (an id AND a name), never neither.
    /// </summary>
    private Error? ValidateLines()
    {
        if (purchaseDate == default)
            return Error.Custom("Intake.MissingPurchaseDate", "Enter the purchase date.");

        if (lines.Count == 0)
            return Error.Custom("Intake.NoLines", "Enter at least one line.");

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lineNo = i + 1;

            if (line.Quantity <= 0m)
                return Error.Custom("Intake.InvalidQuantity", $"Line {lineNo}: quantity must be greater than zero.");
            if (line.Price is < 0m)
                return Error.Custom("Intake.InvalidPrice", $"Line {lineNo}: price can't be negative.");

            var isExisting = line.ProductId is not null;
            var isNew = !string.IsNullOrWhiteSpace(line.NewProductName);
            if (isExisting == isNew)
                return Error.Custom(
                    "Intake.InvalidLineProduct",
                    $"Line {lineNo}: must name either an existing product or a new product, not both or neither.");
            if (isNew && line.NewProductCategoryId is null)
                return Error.Custom("Intake.MissingProductCategory", $"Line {lineNo}: a new product needs a category.");
        }

        return null;
    }

    /// <summary>
    /// Enriches <see cref="CommitSessionCommand"/>'s error with how many of the typed lines actually
    /// committed before the failure (the acceptance requirement: name what did commit, never a bare "commit
    /// failed"). Reads <paramref name="session"/>'s own line statuses — the same tracked instance
    /// <see cref="CommitSessionCommand"/> mutated via the shared <see cref="IImportSessionRepository"/> — so
    /// this reflects the true post-failure state, not a stale in-memory guess. The session itself remains
    /// <c>Ready</c> and stays visible in Intake history either way (unchanged <see cref="CommitSessionCommand"/>
    /// behaviour).
    /// </summary>
    private static Error DescribePartialCommit(ImportSession session, Error commitError)
    {
        var committed = session.Lines.Count(l => l.Status == LineStatus.Committed);
        var total = session.Lines.Count;
        var detail = committed > 0
            ? $"{commitError.Description} {committed} of {total} line(s) were committed before the failure — the rest are still pending in this session, which remains in Intake history."
            : $"{commitError.Description} No lines were committed — the session remains in Intake history.";
        return Error.Custom(commitError.Code, detail);
    }
}
