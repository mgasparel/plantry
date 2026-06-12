using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Shared;
using Plantry.Web.TagHelpers;

namespace Plantry.Web.Pages.Intake;

/// <summary>
/// SPEC §2e — the intake review form. Renders a <c>Ready</c> <see cref="ImportSession"/> and its lines and
/// drives per-line resolution through htmx fragments: each row action (confirm against an existing product,
/// confirm-as-new, dismiss, restore) posts to a handler that returns the OOB-bundle contract (ADR-013 §1) —
/// the one changed row swapped in place via outerHTML, plus OOB fragments for chips/progress/commit-bar/receipt-total.
///
/// <para>The application commands (plantry-kuv) are constructed per-request here rather than injected — only
/// the repository, tenant context and reference-data provider are DI'd. AI-suggested fields are display-only
/// hints; nothing reaches the pantry until the user resolves a line and the session is committed through
/// <see cref="CommitSessionCommand"/> (only Confirmed lines commit; committed lines are never re-written).</para>
/// </summary>
[Authorize]
public sealed class ReviewModel(
    IImportSessionRepository sessions,
    IReviewReferenceDataProvider referenceData,
    ICreateProductPort createProduct,
    IAddStockPort addStock,
    IRecordPricePort recordPrice,
    IClock clock,
    ITenantContext tenant) : PageModel
{
    /// <summary>Session id from the route; bound on GET and round-tripped on every row/commit POST.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public SessionReviewView Session { get; private set; } = null!;

    public IReadOnlyList<ReviewRowModel> Rows { get; private set; } = [];

    public IReadOnlyList<SelectListItem> ProductOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];

    /// <summary>Per-row edit form, model-bound on a save POST. The hidden <see cref="LineId"/> identifies
    /// which line the drawer belongs to; <see cref="CreateNew"/> switches resolution to the §2d create path.</summary>
    [BindProperty]
    public LineEditInput Edit { get; set; } = new();

    public sealed class LineEditInput
    {
        public Guid LineId { get; set; }
        public bool CreateNew { get; set; }
        public Guid? ProductId { get; set; }
        /// <summary>Optional pack-size selection — the user picks the SKU manually; the AI only supplies the product match.</summary>
        public Guid? SkuId { get; set; }
        public string? NewProductName { get; set; }
        public Guid? NewProductCategoryId { get; set; }
        public decimal? Quantity { get; set; }
        public Guid? UnitId { get; set; }
        public Guid? LocationId { get; set; }
        public DateOnly? ExpiryDate { get; set; }
        public decimal? Price { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (loaded is { } failure)
            return failure;

        // The review form only applies to a session the user can still act on. A committed or discarded
        // session has no review to do — send the user to the pantry where the result lives.
        if (Session.Status != ImportStatus.Ready)
            return RedirectToPage("/Pantry/Index");

        return Page();
    }

    // ── Row actions — each returns the ADR-013 OOB-bundle (one row + four aggregate OOB fragments) ──

    /// <summary>Confirm/resolve a line — against an existing catalog product, or (CreateNew) the §2d
    /// create-or-link path. Returns the OOB-bundle on success; a row error on validation failure.</summary>
    public async Task<IActionResult> OnPostSaveLineAsync(CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (loaded is { } failure)
            return failure;

        var lineId = ImportLineId.From(Edit.LineId);

        // Validate the user-resolved fields here, before the command — a confirmed line must carry a real
        // quantity, unit and location (the AI hints are never trusted to fill these). The domain commands
        // take them as required values, so a missing field is surfaced as an inline row error.
        if (Edit.Quantity is not { } quantity || quantity <= 0m)
            return RowError(lineId, "Enter a quantity greater than zero.");
        if (Edit.UnitId is not { } unitId)
            return RowError(lineId, "Choose a unit.");
        if (Edit.LocationId is not { } locationId)
            return RowError(lineId, "Choose a location.");

        Result result;
        if (Edit.CreateNew)
        {
            if (string.IsNullOrWhiteSpace(Edit.NewProductName) || Edit.NewProductCategoryId is null)
                return RowError(lineId, "A new product needs a name and a category.");

            result = await new ConfirmLineAsNewCommand(
                ImportSessionId.From(Id), lineId,
                Edit.NewProductName!, Edit.NewProductCategoryId!.Value,
                quantity, unitId, locationId,
                Edit.ExpiryDate, Edit.Price,
                sessions, tenant).ExecuteAsync(ct);
        }
        else
        {
            if (Edit.ProductId is null)
                return RowError(lineId, "Choose a product, or switch to creating a new one.");

            result = await new ResolveLineCommand(
                ImportSessionId.From(Id), lineId,
                Edit.ProductId!.Value, skuId: Edit.SkuId,
                quantity, unitId, locationId,
                Edit.ExpiryDate, Edit.Price,
                sessions, tenant).ExecuteAsync(ct);
        }

        return await RowResultAsync(lineId, result, ct);
    }

    public async Task<IActionResult> OnPostDismissLineAsync(Guid lineId, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (loaded is { } failure)
            return failure;

        var id = ImportLineId.From(lineId);
        var result = await new DismissLineCommand(
            ImportSessionId.From(Id), id, sessions, tenant).ExecuteAsync(ct);
        return await RowResultAsync(id, result, ct);
    }

    /// <summary>Restore a dismissed line back to Pending so the user can resolve it again ("Add anyway").</summary>
    public async Task<IActionResult> OnPostRestoreLineAsync(Guid lineId, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (loaded is { } failure)
            return failure;

        var id = ImportLineId.From(lineId);
        var result = await new RestoreLineCommand(
            ImportSessionId.From(Id), id, sessions, tenant).ExecuteAsync(ct);
        return await RowResultAsync(id, result, ct);
    }

    // ── Commit / discard ────────────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostCommitAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        var result = await new CommitSessionCommand(
            ImportSessionId.From(Id), sessions, createProduct, addStock, recordPrice, clock, tenant)
            .ExecuteAsync(ct);

        if (result.IsFailure)
        {
            var loaded = await LoadAsync(ct);
            if (loaded is { } failure)
                return failure;
            return CommitBarError(result.Error.Description);
        }

        // Committed — the new stock now lives in the pantry. Tell htmx to do a full client-side redirect
        // to the Done screen (an htmx response header, since the Commit button posts via hx-post).
        Response.Headers["HX-Redirect"] = Url.Page("/Intake/Done", new { Id })!;
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostDiscardAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        var result = await new DiscardSessionCommand(
            ImportSessionId.From(Id), sessions, tenant).ExecuteAsync(ct);

        if (result.IsFailure)
        {
            var loaded = await LoadAsync(ct);
            if (loaded is { } failure)
                return failure;
            return CommitBarError(result.Error.Description);
        }

        Response.Headers["HX-Redirect"] = Url.Page("/Pantry/Index")!;
        return new EmptyResult();
    }

    // ── searchable-select product filter (htmx) ──────────────────────────────────────────────────

    public async Task<ContentResult> OnGetFilterProductsAsync(string? q, CancellationToken ct)
    {
        var reference = await referenceData.GetAsync(ct);
        var matches = reference.Products
            .Where(p => string.IsNullOrWhiteSpace(q) || p.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(p => new SelectListItem(p.Name, p.Id.ToString()));

        var html = new System.Text.StringBuilder();
        SearchableSelectTagHelper.AppendOptions(html, matches, System.Text.Encodings.Web.HtmlEncoder.Default);
        return Content(html.ToString(), "text/html");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads the session, lines and reference data via the application query, populating page
    /// state. Returns a non-null <see cref="IActionResult"/> only on failure (NotFound / Forbid) — null on
    /// success — so callers can short-circuit with <c>if (await LoadAsync(ct) is { } failure) return failure;</c>.</summary>
    private async Task<IActionResult?> LoadAsync(CancellationToken ct)
    {
        var query = new GetSessionForReviewQuery(
            ImportSessionId.From(Id), sessions, referenceData, tenant);
        var result = await query.ExecuteAsync(ct);

        if (result.IsFailure)
            return result.Error.Code == Error.Unauthorized.Code ? Forbid() : NotFound();

        Session = result.Value;
        var reference = Session.ReferenceData;

        ProductOptions = reference.Products
            .Select(p => new SelectListItem(p.Name, p.Id.ToString()))
            .ToList();
        UnitOptions = reference.Units
            .Select(u => new SelectListItem($"{u.Code} — {u.Name}", u.Id.ToString()))
            .ToList();
        LocationOptions = reference.Locations
            .Select(l => new SelectListItem(l.Name, l.Id.ToString()))
            .ToList();
        CategoryOptions = reference.Categories
            .Select(c => new SelectListItem(c.Name, c.Id.ToString()))
            .ToList();

        var unitCodeById = reference.Units.ToDictionary(u => u.Id, u => u.Code);
        var unitIdByCode = reference.Units.ToDictionary(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase);
        var productNameById = reference.Products.ToDictionary(p => p.Id, p => p.Name);
        var productDefaultLocationById = reference.Products.ToDictionary(p => p.Id, p => p.DefaultLocationId);
        var locationNameById = reference.Locations.ToDictionary(l => l.Id, l => l.Name);

        // Build a productId → SKU-options map for the drawer; only products that have SKUs are included.
        var skusByProductId = reference.Products
            .Where(p => p.Skus.Count > 0)
            .ToDictionary(
                p => p.Id.ToString(),
                p => (IReadOnlyList<ReviewSkuOption>)p.Skus);

        Rows = Session.Lines
            .Select(l => ReviewRowModel.From(l, Url, Id, unitCodeById, unitIdByCode, productNameById, productDefaultLocationById, locationNameById, skusByProductId))
            .ToList();

        return null;
    }

    /// <summary>Renders one row's <c>_ReviewRow</c> fragment (the htmx swap target), optionally with an inline
    /// error banner. The partial's model is the page itself so its model-bound <c>&lt;searchable-select&gt;</c>
    /// resolves; the specific row is passed via <c>ViewData["Row"]</c>.</summary>
    private IActionResult RowPartial(ImportLineId lineId, string? error = null)
    {
        var row = Rows.FirstOrDefault(r => r.Line.LineId == lineId.Value);
        if (row is null)
            return NotFound();
        ViewData["Row"] = row;
        ViewData["RowError"] = error;
        // PageModel.Partial(name, model) builds a fresh ViewDataDictionary, dropping the entries set above
        // (Row / RowError) that _ReviewRow reads. Return the result directly so this page's ViewData — with
        // the row and the bind target (this) — flows into the fragment.
        return new PartialViewResult { ViewName = "_ReviewRow", ViewData = ViewData, TempData = TempData };
    }

    /// <summary>
    /// ADR-013 §1 — the row-action response contract. On a successful command: sets HX-Retarget/HX-Reswap
    /// so htmx swaps the one changed row in place (outerHTML on #import-line-{id}), then returns
    /// <c>_ReviewRowOobBundle</c> which emits the row fragment + the four OOB projection fragments
    /// (chips, progress, commit bar, receipt total). On failure: delegates to <see cref="RowError"/>.
    /// </summary>
    private async Task<IActionResult> RowResultAsync(ImportLineId lineId, Result result, CancellationToken ct)
    {
        if (result.IsFailure)
            return RowError(lineId, result.Error.Description);

        await LoadAsync(ct);

        // Retarget to the specific row so htmx swaps the primary swap body as outerHTML on #import-line-{id}.
        // The OOB fragments in _ReviewRowOobBundle land independently in their own regions.
        Response.Headers["HX-Retarget"] = $"#import-line-{lineId.Value}";
        Response.Headers["HX-Reswap"] = "outerHTML";

        var row = Rows.FirstOrDefault(r => r.Line.LineId == lineId.Value);
        if (row is null)
            return NotFound();
        ViewData["Row"] = row;
        return new PartialViewResult { ViewName = "_ReviewRowOobBundle", ViewData = ViewData, TempData = TempData };
    }

    private IActionResult RowError(ImportLineId lineId, string message)
    {
        // A rejected edit hasn't changed any line's state, so chips and aggregates don't move —
        // re-render only the offending row (drawer reopened) and retarget htmx back to it.
        // 200 (not 422) so htmx performs the swap at all (htmx 2.x blocks swaps on 4xx by default).
        Response.Headers["HX-Retarget"] = $"#import-line-{lineId.Value}";
        Response.Headers["HX-Reswap"] = "outerHTML";
        return RowPartial(lineId, message);
    }

    private IActionResult CommitBarError(string message)
    {
        // 200 so htmx swaps the alert (same reason as RowError).
        ViewData["AlertMessage"] = message;
        // As in RowPartial: return directly so the page's ViewData (carrying AlertMessage) reaches the fragment.
        return new PartialViewResult { ViewName = "_ReviewAlert", ViewData = ViewData, TempData = TempData };
    }

    // ── Row partitioning + projections (ADR-013 §2) ──────────────────────────────────────────────
    // Single source of truth for all page-level aggregates. Both the initial render (via _ReviewBody)
    // and every row-action response (via _ReviewRowOobBundle) call BuildProjections() — they cannot drift.

    public IReadOnlyList<ReviewRowModel> NeedsReviewRows =>
        Rows.Where(r => !r.RowViewModel.IsConfirmed && !r.RowViewModel.IsCommitted && !r.RowViewModel.IsDismissed).ToList();

    public IReadOnlyList<ReviewRowModel> ReadyRows =>
        Rows.Where(r => r.RowViewModel.IsConfirmed || r.RowViewModel.IsCommitted).ToList();

    public IReadOnlyList<ReviewRowModel> SkippedRows =>
        Rows.Where(r => r.RowViewModel.IsDismissed).ToList();

    /// <summary>
    /// ADR-013 §2 — single projection builder. Collapses the four previously-scattered aggregate
    /// computations (receipt total in Review.cshtml, BuildProgress(), BuildCommitBar(), chip counts
    /// in _ReviewBody) into one typed record. Both the initial render and every row-action response
    /// call this; the two renders are guaranteed to compute the same aggregates.
    /// </summary>
    public ReviewProjections BuildProjections()
    {
        var needsRows = NeedsReviewRows;
        var readyRows = ReadyRows;
        var skippedRows = SkippedRows;
        var total = needsRows.Count + readyRows.Count;

        var progress = new ReviewProgress(needsRows.Count, readyRows.Count);

        // Committable = non-dismissed lines; value comes from confirmed + committed lines.
        var committable = Session.Lines.Where(l => l.Status != LineStatus.Dismissed).ToList();
        var confirmedLines = committable.Where(l => l.Status is LineStatus.Confirmed or LineStatus.Committed).ToList();
        var confirmedValue = confirmedLines.Sum(l => l.Price ?? l.SuggestedPrice ?? 0m);
        var commitBar = new CommitBarViewModel(
            Confirmed: confirmedLines.Count,
            Total: committable.Count,
            ConfirmedValue: confirmedValue,
            CommitUrl: Url.Page("./Review", "Commit", new { Id })!,
            DiscardUrl: Url.Page("./Review", "Discard", new { Id })!);

        // Receipt total = sum of non-dismissed lines (includes unconfirmed Pending lines).
        var receiptTotal = Session.Lines
            .Where(l => l.Status != LineStatus.Dismissed)
            .Sum(l => l.Price ?? l.SuggestedPrice ?? 0m);

        return new ReviewProjections(
            NeedsCount: needsRows.Count,
            ReadyCount: readyRows.Count,
            SkippedCount: skippedRows.Count,
            TotalItems: total,
            Progress: progress,
            CommitBar: commitBar,
            ReceiptTotal: receiptTotal,
            StoreLabel: StoreLabel,
            SessionDate: Session.CreatedAt.ToLocalTime().ToString("ddd MMM d, yyyy", CultureInfo.CurrentCulture));
    }

    /// <summary>Merchant label shown in both the receipt panel (Review.cshtml) and the review header —
    /// the parsed merchant text, or "Receipt" when the parser captured none. Computed once here so the
    /// two panels can't display different store names.</summary>
    public string StoreLabel =>
        string.IsNullOrWhiteSpace(Session.MerchantText) ? "Receipt" : Session.MerchantText;

    // ── Legacy helpers kept for backward-compat until all callers use BuildProjections() ─────────

    public ReviewProgress BuildProgress() => new(NeedsReviewRows.Count, ReadyRows.Count);

    public CommitBarViewModel BuildCommitBar()
    {
        var committable = Session.Lines.Where(l => l.Status != LineStatus.Dismissed).ToList();
        var confirmedLines = committable.Where(l => l.Status is LineStatus.Confirmed or LineStatus.Committed).ToList();
        var confirmedValue = confirmedLines.Sum(l => l.Price ?? l.SuggestedPrice ?? 0m);
        return new CommitBarViewModel(
            Confirmed: confirmedLines.Count,
            Total: committable.Count,
            ConfirmedValue: confirmedValue,
            CommitUrl: Url.Page("./Review", "Commit", new { Id })!,
            DiscardUrl: Url.Page("./Review", "Discard", new { Id })!);
    }
}

/// <summary>
/// ADR-013 §2 — the single typed projection record for all page-level aggregates. Returned by
/// <see cref="ReviewModel.BuildProjections()"/> and consumed by both the initial render
/// (_ReviewBody / Review.cshtml) and every row-action OOB response (_ReviewRowOobBundle).
/// </summary>
public sealed record ReviewProjections(
    int NeedsCount,
    int ReadyCount,
    int SkippedCount,
    int TotalItems,
    ReviewProgress Progress,
    CommitBarViewModel CommitBar,
    decimal ReceiptTotal,
    string StoreLabel,
    string SessionDate);

/// <summary>The review header's progress summary — how many lines still need a look versus are ready to
/// commit, plus the derived percentage and commit-eligibility. Consumed by both the initial render and
/// the OOB progress fragment (_ReviewProgressOob).</summary>
public sealed record ReviewProgress(int NeedsCount, int ReadyCount)
{
    public int Total => NeedsCount + ReadyCount;
    public int Percent => Total > 0 ? (int)Math.Round((double)ReadyCount / Total * 100) : 100;
    public bool CanCommit => NeedsCount == 0 && Total > 0;
}

/// <summary>Couples the shared <see cref="ImportLineRowViewModel"/> (the swap-target VM the partial needs) with
/// the original <see cref="ReviewLineView"/> so the page can pre-populate the full edit drawer (unit, location,
/// category, price — fields the shared partial's lightweight drawer does not carry).</summary>
public sealed record ReviewRowModel(
    ReviewLineView Line,
    ImportLineRowViewModel RowViewModel,
    Guid? PrefillProductId,
    string? PrefillProductName,
    decimal? PrefillQuantity,
    Guid? PrefillUnitId,
    Guid? PrefillLocationId,
    string? PrefillLocationName,
    decimal? PrefillPrice,
    /// <summary>Map of productId → list of SKU options for all products that have SKUs — embedded in the
    /// drawer as JSON so Alpine can filter pack-size choices when the product selection changes.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<ReviewSkuOption>> SkusByProductId,
    Guid? PrefillSkuId,
    /// <summary>Ranked alternative candidates resolved against the household catalog — only set when there
    /// are two or more credible candidates (mirrors the Line.SuggestedAlternatives gate). The product id
    /// in each candidate is already resolved: candidates whose parser name did not match any catalog product
    /// are excluded so the drawer never shows an unresolvable suggestion button.</summary>
    IReadOnlyList<ReviewAlternativeCandidate>? Alternatives = null)
{
    /// <summary>
    /// Pure prefill computation — no URL or HTTP context needed. Applies the priority chain:
    /// user-resolved fields first, AI suggestions for Pending lines as fallback. Only uses
    /// <paramref name="unitIdByCode"/> (label → Guid) and <paramref name="productNameById"/>
    /// (Guid → name); the display unit code lookup uses the broader <c>unitCodeById</c> in
    /// <see cref="From"/> after this call.
    /// </summary>
    public static (Guid? ProductId, string? ProductName, decimal? Qty, Guid? UnitId, Guid? LocationId, decimal? Price) ComputePrefill(
        ReviewLineView line,
        IReadOnlyDictionary<string, Guid> unitIdByCode,
        IReadOnlyDictionary<Guid, string> productNameById,
        IReadOnlyDictionary<Guid, Guid?> productDefaultLocationById)
    {
        var isPending = line.Status == LineStatus.Pending;

        // Pre-fill product: user-resolved first; for Pending lines fall back to AI suggestion,
        // but only when the suggested ID actually resolves in the catalog — a phantom ID would
        // show a name in the row summary while leaving the drawer's product dropdown empty.
        Guid? prefillProductId = line.IsNewProduct ? null
            : line.ProductId
              ?? (isPending && line.SuggestedProductId is { } sugPid && productNameById.ContainsKey(sugPid)
                  ? sugPid : (Guid?)null);

        string? prefillProductName = line.IsNewProduct ? null
            : prefillProductId is { } ppid && productNameById.TryGetValue(ppid, out var ppname)
                ? ppname
                : (isPending ? line.SuggestedProductName : null);

        // Pre-fill qty/unit/price: user-resolved first; fall back to AI suggestion for Pending lines.
        var prefillQty = line.Quantity ?? (isPending ? line.SuggestedQuantity : null);

        Guid? prefillUnitId = line.UnitId
            ?? (isPending && line.SuggestedUnitLabel is { } lbl && unitIdByCode.TryGetValue(lbl, out var sugUid)
                ? sugUid
                : (Guid?)null);

        // Pre-fill location: user-resolved first; fall back to the matched product's default for Pending lines.
        Guid? prefillLocationId = line.LocationId
            ?? (isPending && prefillProductId is { } locPid && productDefaultLocationById.TryGetValue(locPid, out var defLoc)
                ? defLoc
                : (Guid?)null);

        var prefillPrice = line.Price ?? (isPending ? line.SuggestedPrice : null);

        return (prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillLocationId, prefillPrice);
    }

    public static ReviewRowModel From(
        ReviewLineView line,
        IUrlHelper url,
        Guid sessionId,
        IReadOnlyDictionary<Guid, string> unitCodeById,
        IReadOnlyDictionary<string, Guid> unitIdByCode,
        IReadOnlyDictionary<Guid, string> productNameById,
        IReadOnlyDictionary<Guid, Guid?> productDefaultLocationById,
        IReadOnlyDictionary<Guid, string> locationNameById,
        IReadOnlyDictionary<string, IReadOnlyList<ReviewSkuOption>> skusByProductId)
    {
        var (prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillLocationId, prefillPrice) =
            ComputePrefill(line, unitIdByCode, productNameById, productDefaultLocationById);

        var prefillLocationName = prefillLocationId is { } locId && locationNameById.TryGetValue(locId, out var locName)
            ? locName
            : null;

        // Display name: new-product intent uses the chosen name; everything else uses the pre-fill.
        string? productName = line.IsNewProduct ? line.NewProductName : prefillProductName;

        var unitCode = prefillUnitId is { } uid && unitCodeById.TryGetValue(uid, out var code) ? code : "";

        var vm = new ImportLineRowViewModel(
            LineId: line.LineId.ToString(),
            ProductName: productName,
            RawText: line.ReceiptText,
            Confidence: line.SuggestedConfidence,
            Status: line.Status,
            Quantity: prefillQty?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—",
            Unit: unitCode,
            Price: prefillPrice is { } p ? p.ToString("C", CultureInfo.CurrentCulture) : "—",
            Expiry: line.ExpiryDate?.ToString("d MMM", CultureInfo.CurrentCulture) ?? "—",
            CreatedNew: line.IsNewProduct,
            ConfirmUrl: url.Page("./Review", "SaveLine", new { Id = sessionId })!,
            DismissUrl: url.Page("./Review", "DismissLine", new { Id = sessionId, lineId = line.LineId })!,
            RestoreUrl: url.Page("./Review", "RestoreLine", new { Id = sessionId, lineId = line.LineId })!,
            SaveUrl: url.Page("./Review", "SaveLine", new { Id = sessionId })!);

        // Resolve alternative candidates against the household catalog — only include candidates whose
        // parser-supplied name maps to a real product id so the suggestion button always resolves.
        // Alternatives are only surfaced when there are 2+ credible options (the gate is also applied
        // in GetSessionForReviewQuery, but we re-check here so From() is self-contained).
        IReadOnlyList<ReviewAlternativeCandidate>? alternatives = null;
        if (line.SuggestedAlternatives is { Count: >= 2 } alts)
        {
            var resolved = alts
                .Where(a => a.ProductId is { } p && productNameById.ContainsKey(p))
                .Select(a =>
                {
                    // ProductId is non-null and verified above; resolve the catalog display name.
                    var label = productNameById.TryGetValue(a.ProductId!.Value, out var n) ? n : a.ProductName;
                    return new ReviewAlternativeCandidate(a.ProductId!.Value, label, a.Confidence);
                })
                .ToList();

            if (resolved.Count >= 2)
                alternatives = resolved;
        }

        return new ReviewRowModel(line, vm, prefillProductId, prefillProductName, prefillQty, prefillUnitId,
            prefillLocationId, prefillLocationName, prefillPrice, skusByProductId, PrefillSkuId: line.SkuId,
            Alternatives: alternatives);
    }
}

/// <summary>
/// A catalog-resolved alternative candidate for the "Did you mean" suggestion block in the review drawer.
/// Shown only when the line has two or more credible alternatives and is not yet confirmed.
/// ProductId is always resolved — candidates without a matching catalog entry are filtered out in
/// <see cref="ReviewRowModel.From"/>, so no suggestion button can produce an empty product selection.
/// </summary>
public sealed record ReviewAlternativeCandidate(
    Guid ProductId,
    string ProductName,
    decimal Confidence);
