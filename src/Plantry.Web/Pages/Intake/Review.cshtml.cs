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
/// confirm-as-new, dismiss, restore) posts to a handler that re-renders the single <c>_ImportLineRow</c>
/// partial as the swap response, so fragments stay independently renderable (plantry-qzq).
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

    // ── Row actions — each re-renders one _ImportLineRow as the htmx swap response ──────────────────

    /// <summary>Confirm/resolve a line — against an existing catalog product, or (CreateNew) the §2d
    /// create-or-link path. Re-renders the single row fragment.</summary>
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
                Edit.ProductId!.Value, skuId: null,
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
        // (an htmx response header, since the Commit button posts via hx-post).
        Response.Headers["HX-Redirect"] = Url.Page("/Pantry/Index")!;
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

        Rows = Session.Lines
            .Select(l => ReviewRowModel.From(l, Url, Id, unitCodeById, unitIdByCode, productNameById))
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

    /// <summary>Re-renders the row reflecting a successful command, or surfaces the command's error inline on
    /// the row (re-rendered with the original state plus an error banner so the failure stays in context).</summary>
    private async Task<IActionResult> RowResultAsync(ImportLineId lineId, Result result, CancellationToken ct)
    {
        if (result.IsFailure)
            return RowError(lineId, result.Error.Description);

        await LoadAsync(ct);
        ViewData["IncludeCommitBarOob"] = true;
        return RowPartial(lineId);
    }

    private IActionResult RowError(ImportLineId lineId, string message)
    {
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return RowPartial(lineId, message);
    }

    private IActionResult CommitBarError(string message)
    {
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        ViewData["AlertMessage"] = message;
        // As in RowPartial: return directly so the page's ViewData (carrying AlertMessage) reaches the fragment.
        return new PartialViewResult { ViewName = "_ReviewAlert", ViewData = ViewData, TempData = TempData };
    }

    public CommitBarViewModel BuildCommitBar()
    {
        // "Total" is the set of committable (non-dismissed) lines; "Confirmed" counts those resolved. Already
        // committed lines count as confirmed (resumability). Dismissed lines are excluded from both.
        var committable = Session.Lines.Where(l => l.Status != LineStatus.Dismissed).ToList();
        var confirmed = committable.Count(l => l.Status is LineStatus.Confirmed or LineStatus.Committed);
        return new CommitBarViewModel(
            Confirmed: confirmed,
            Total: committable.Count,
            CommitUrl: Url.Page("./Review", "Commit", new { Id })!,
            DiscardUrl: Url.Page("./Review", "Discard", new { Id })!);
    }
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
    decimal? PrefillPrice)
{
    /// <summary>
    /// Pure prefill computation — no URL or HTTP context needed. Applies the priority chain:
    /// user-resolved fields first, AI suggestions for Pending lines as fallback. Only uses
    /// <paramref name="unitIdByCode"/> (label → Guid) and <paramref name="productNameById"/>
    /// (Guid → name); the display unit code lookup uses the broader <c>unitCodeById</c> in
    /// <see cref="From"/> after this call.
    /// </summary>
    public static (Guid? ProductId, string? ProductName, decimal? Qty, Guid? UnitId, decimal? Price) ComputePrefill(
        ReviewLineView line,
        IReadOnlyDictionary<string, Guid> unitIdByCode,
        IReadOnlyDictionary<Guid, string> productNameById)
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

        var prefillPrice = line.Price ?? (isPending ? line.SuggestedPrice : null);

        return (prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillPrice);
    }

    public static ReviewRowModel From(
        ReviewLineView line,
        IUrlHelper url,
        Guid sessionId,
        IReadOnlyDictionary<Guid, string> unitCodeById,
        IReadOnlyDictionary<string, Guid> unitIdByCode,
        IReadOnlyDictionary<Guid, string> productNameById)
    {
        var (prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillPrice) =
            ComputePrefill(line, unitIdByCode, productNameById);

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

        return new ReviewRowModel(line, vm, prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillPrice);
    }
}
