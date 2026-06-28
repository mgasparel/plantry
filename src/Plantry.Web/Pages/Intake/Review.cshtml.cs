using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
namespace Plantry.Web.Pages.Intake;

/// <summary>
/// SPEC §2e — the intake review form. Renders a <c>Ready</c> <see cref="ImportSession"/> as a
/// Preact island (ADR-020, plantry-2zvm.3) that owns all UI/draft/derived state. The OOB-bundle
/// machinery (ADR-013) is retired for this surface — a reactive runtime makes derived-view drift
/// structurally impossible.
///
/// <para>GET: loads the session and builds the full hydration JSON that the island reads on mount.
/// The hydration includes session header, reference data (products with defaults+skus, units,
/// locations, categories), and per-line { line, prefill } objects where prefill is computed
/// server-side via <see cref="ReviewRowModel.ComputePrefill"/> (the priority chain stays here,
/// per ADR-020 §3 / Boundary judgment call 1).</para>
///
/// <para>POST endpoints return JSON (ADR-015 amendment: island data endpoints return JSON):
///   SaveLine    → { status, isNewProduct, newProductName, productId, productName, price } | { error }
///   DismissLine → { status } | { error }
///   RestoreLine → { status } | { error }
///   Commit      → { redirectUrl } | { error }
///   Discard     → { redirectUrl } | { error }
/// </para>
///
/// <para>The application commands are constructed per-request here rather than injected — only
/// the repository, tenant context and reference-data provider are DI'd. AI-suggested fields are
/// display-only hints; nothing reaches the pantry until the user resolves a line and the session
/// is committed through <see cref="CommitSessionCommand"/> (only Confirmed lines commit; committed
/// lines are never re-written).</para>
/// </summary>
[Authorize]
public sealed class ReviewModel(
    IImportSessionRepository sessions,
    IReviewReferenceDataProvider referenceData,
    ICreateProductPort createProduct,
    IAddStockPort addStock,
    IRecordPricePort recordPrice,
    IClock clock,
    ITenantContext tenant,
    ILogger<CommitSessionCommand> commitLogger,
    ILogger<DiscardSessionCommand> discardLogger,
    ILogger<DismissLineCommand> dismissLogger,
    ILogger<ResolveLineCommand> resolveLogger,
    ILogger<ConfirmLineAsNewCommand> confirmAsNewLogger,
    ILogger<RestoreLineCommand> restoreLogger) : PageModel
{
    /// <summary>Session id from the route; bound on GET and carried on every POST via the URL.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public SessionReviewView Session { get; private set; } = null!;

    /// <summary>Today's date derived from the injected <see cref="IClock"/> — used in the hydration JSON.</summary>
    public DateOnly Today { get; private set; }

    /// <summary>Island hydration JSON — embedded in the page by Review.cshtml as a
    /// &lt;script type="application/json"&gt; block for zero-round-trip island mount.</summary>
    public string IslandHydrationJson { get; private set; } = "null";

    /// <summary>Per-row edit form shape — kept for JSON deserialization on POST.</summary>
    public sealed class LineEditInput
    {
        public Guid LineId { get; set; }
        public bool CreateNew { get; set; }
        public Guid? ProductId { get; set; }
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

        if (Session.Status != ImportStatus.Ready)
            return RedirectToPage("/Pantry/Index");

        IslandHydrationJson = BuildHydrationJson();
        return Page();
    }

    // ── Row actions — JSON endpoints (ADR-015 amendment: island data endpoints return JSON) ──

    /// <summary>Confirm/resolve a line — against an existing catalog product, or (CreateNew) the §2d
    /// create-or-link path. Returns updated line state as JSON on success; an error JSON on failure.
    /// The server re-validates all fields and is authoritative (Boundary judgment call 3).
    /// Id is route-bound (from /Intake/Review/{id:guid}?handler=SaveLine).</summary>
    public async Task<IActionResult> OnPostSaveLineAsync(CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (loaded is { } failure)
            return failure;

        LineEditInput edit;
        try
        {
            edit = await ReadJsonBodyAsync<LineEditInput>(ct) ?? new LineEditInput();
        }
        catch
        {
            return JsonError("Invalid request body.");
        }

        var lineId = ImportLineId.From(edit.LineId);

        if (edit.Quantity is not { } quantity || quantity <= 0m)
            return JsonError("Enter a quantity greater than zero.");
        if (edit.UnitId is not { } unitId)
            return JsonError("Choose a unit.");
        if (edit.LocationId is not { } locationId)
            return JsonError("Choose a location.");

        Result result;
        if (edit.CreateNew)
        {
            if (string.IsNullOrWhiteSpace(edit.NewProductName) || edit.NewProductCategoryId is null)
                return JsonError("A new product needs a name and a category.");

            result = await new ConfirmLineAsNewCommand(
                ImportSessionId.From(Id), lineId,
                edit.NewProductName!, edit.NewProductCategoryId!.Value,
                quantity, unitId, locationId,
                edit.ExpiryDate, edit.Price,
                sessions, tenant, confirmAsNewLogger).ExecuteAsync(ct);
        }
        else
        {
            if (edit.ProductId is null)
                return JsonError("Choose a product, or switch to creating a new one.");

            result = await new ResolveLineCommand(
                ImportSessionId.From(Id), lineId,
                edit.ProductId!.Value, skuId: edit.SkuId,
                quantity, unitId, locationId,
                edit.ExpiryDate, edit.Price,
                sessions, tenant, resolveLogger).ExecuteAsync(ct);
        }

        if (result.IsFailure)
            return JsonError(result.Error.Description);

        // Reload to get the updated line state
        await LoadAsync(ct);
        var updated = Session.Lines.FirstOrDefault(l => l.LineId == lineId.Value);
        if (updated is null)
            return JsonError("Line not found after save.");

        return new JsonResult(new
        {
            status = updated.Status.ToString(),
            isNewProduct = updated.IsNewProduct,
            newProductName = updated.NewProductName,
            productId = updated.ProductId?.ToString(),
            productName = updated.ProductId is { } pid
                ? Session.ReferenceData.Products.FirstOrDefault(p => p.Id == pid)?.Name
                : null,
            price = updated.Price ?? updated.SuggestedPrice,
            error = (string?)null,
        });
    }

    public async Task<IActionResult> OnPostDismissLineAsync([FromQuery] Guid lineId, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (loaded is { } failure)
            return failure;

        var id = ImportLineId.From(lineId);
        var result = await new DismissLineCommand(
            ImportSessionId.From(Id), id, sessions, tenant, dismissLogger).ExecuteAsync(ct);

        if (result.IsFailure)
            return JsonError(result.Error.Description);

        return new JsonResult(new { status = "Dismissed", error = (string?)null });
    }

    public async Task<IActionResult> OnPostRestoreLineAsync([FromQuery] Guid lineId, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (loaded is { } failure)
            return failure;

        var id = ImportLineId.From(lineId);
        var result = await new RestoreLineCommand(
            ImportSessionId.From(Id), id, sessions, tenant, restoreLogger).ExecuteAsync(ct);

        if (result.IsFailure)
            return JsonError(result.Error.Description);

        return new JsonResult(new { status = "Pending", error = (string?)null });
    }

    // ── Commit / discard — return redirect target so the island can navigate ────────

    public async Task<IActionResult> OnPostCommitAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is null)
            return JsonError("Unauthorized.");

        var result = await new CommitSessionCommand(
            ImportSessionId.From(Id), sessions, createProduct, addStock, recordPrice, clock, tenant, commitLogger)
            .ExecuteAsync(ct);

        if (result.IsFailure)
            return JsonError(result.Error.Description);

        return new JsonResult(new
        {
            redirectUrl = Url.Page("/Intake/Done", new { Id })!,
            error = (string?)null,
        });
    }

    public async Task<IActionResult> OnPostDiscardAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is null)
            return JsonError("Unauthorized.");

        var result = await new DiscardSessionCommand(
            ImportSessionId.From(Id), sessions, tenant, discardLogger).ExecuteAsync(ct);

        if (result.IsFailure)
            return JsonError(result.Error.Description);

        return new JsonResult(new
        {
            redirectUrl = Url.Page("/Pantry/Index")!,
            error = (string?)null,
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private async Task<IActionResult?> LoadAsync(CancellationToken ct)
    {
        var query = new GetSessionForReviewQuery(
            ImportSessionId.From(Id), sessions, referenceData, tenant);
        var result = await query.ExecuteAsync(ct);

        if (result.IsFailure)
            return result.Error.Code == Error.Unauthorized.Code ? Forbid() : NotFound();

        Session = result.Value;
        Today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        return null;
    }

    /// <summary>Builds the full island hydration JSON from the loaded session and reference data.
    /// Must be called after <see cref="LoadAsync"/> succeeds. This is the single emission point
    /// for the island's initial state — the priority chain lives here, not in the island.</summary>
    private string BuildHydrationJson()
    {
        var reference = Session.ReferenceData;
        var today = Today;

        // Products — include defaults so the island can fill empty unit/location/expiry
        // on product re-selection (Boundary judgment call 2: form-filling from held data = UI, allowed).
        var products = reference.Products.Select(p => new ProductHydration(
            Id: p.Id.ToString(),
            Name: p.Name,
            Skus: p.Skus.Select(s => new SkuOption(s.Id.ToString(), s.Label)).ToList(),
            Defaults: new ProductDefaults(
                UnitId: p.DefaultUnitId.ToString(),
                LocationId: p.DefaultLocationId?.ToString(),
                Expiry: p.DefaultDueDays is { } n ? today.AddDays(n).ToString("yyyy-MM-dd") : null))).ToList();

        var units = reference.Units
            .Select(u => new UnitHydration(u.Id.ToString(), u.Code, u.Name)).ToList();

        var locations = reference.Locations
            .Select(l => new LocationHydration(l.Id.ToString(), l.Name)).ToList();

        var categories = reference.Categories
            .Select(c => new CategoryHydration(c.Id.ToString(), c.Name, c.Hue)).ToList();

        var unitIdByCode = reference.Units.ToDictionary(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase);
        var productNameById = reference.Products.ToDictionary(p => p.Id, p => p.Name);
        var productDefaultLocationById = reference.Products.ToDictionary(p => p.Id, p => p.DefaultLocationId);
        var productDefaultUnitById = reference.Products.ToDictionary(p => p.Id, p => p.DefaultUnitId);
        var productDefaultDueDaysById = reference.Products.ToDictionary(p => p.Id, p => p.DefaultDueDays);

        var skusByProductId = reference.Products
            .Where(p => p.Skus.Count > 0)
            .ToDictionary(
                p => p.Id.ToString(),
                p => (IReadOnlyList<ReviewSkuOption>)p.Skus);

        // Per-line: line data + server-computed prefill (Boundary judgment call 1: chain stays server-side)
        var lines = Session.Lines.Select(l =>
        {
            var (prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillLocationId, prefillPrice, prefillExpiry) =
                ReviewRowModel.ComputePrefill(l, unitIdByCode, productNameById, productDefaultLocationById,
                    productDefaultUnitById, productDefaultDueDaysById, today);

            // Alternatives: only resolved catalog entries, 2+ required
            IReadOnlyList<AlternativeHydration>? alternatives = null;
            if (l.SuggestedAlternatives is { Count: >= ImportLine.MinAlternativesForSuggestion } alts)
            {
                var resolved = alts
                    .Where(a => a.ProductId is { } p && productNameById.ContainsKey(p))
                    .Select(a => new AlternativeHydration(
                        ProductId: a.ProductId!.Value.ToString(),
                        ProductName: productNameById.TryGetValue(a.ProductId!.Value, out var n) ? n : a.ProductName,
                        Confidence: a.Confidence))
                    .ToList();
                if (resolved.Count >= ImportLine.MinAlternativesForSuggestion)
                    alternatives = resolved;
            }

            return new LineHydration(
                Line: new LineSeed(
                    LineId: l.LineId.ToString(),
                    ReceiptText: l.ReceiptText,
                    Confidence: l.SuggestedConfidence.ToString(),
                    Status: l.Status.ToString(),
                    ProductId: l.ProductId?.ToString(),
                    SkuId: l.SkuId?.ToString(),
                    Quantity: l.Quantity,
                    UnitId: l.UnitId?.ToString(),
                    LocationId: l.LocationId?.ToString(),
                    ExpiryDate: l.ExpiryDate?.ToString("yyyy-MM-dd"),
                    Price: l.Price,
                    IsNewProduct: l.IsNewProduct,
                    NewProductName: l.NewProductName,
                    NewProductCategoryId: l.NewProductCategoryId?.ToString(),
                    SuggestedPrice: l.SuggestedPrice),
                Prefill: new PrefillData(
                    ProductId: prefillProductId?.ToString(),
                    ProductName: prefillProductName,
                    Quantity: prefillQty,
                    UnitId: prefillUnitId?.ToString(),
                    LocationId: prefillLocationId?.ToString(),
                    Price: prefillPrice,
                    Expiry: prefillExpiry?.ToString("yyyy-MM-dd"),
                    SkuId: l.SkuId?.ToString()),
                Alternatives: alternatives);
        }).ToList();

        var hydration = new SessionHydration(
            MerchantText: string.IsNullOrWhiteSpace(Session.MerchantText) ? "Receipt" : Session.MerchantText,
            SessionDate: Session.CreatedAt.ToLocalTime().ToString("ddd MMM d, yyyy", CultureInfo.CurrentCulture),
            Today: today.ToString("yyyy-MM-dd"),
            CommitUrl: Url.Page("./Review", "Commit", new { Id })!,
            DiscardUrl: Url.Page("./Review", "Discard", new { Id })!,
            SaveLineUrl: Url.Page("./Review", "SaveLine", new { Id })!,
            DismissLineUrl: Url.Page("./Review", "DismissLine", new { Id })!,
            RestoreLineUrl: Url.Page("./Review", "RestoreLine", new { Id })!,
            Products: products,
            Units: units,
            Locations: locations,
            Categories: categories,
            Lines: lines);

        return JsonSerializer.Serialize(hydration, IntakeHydrationJson.Options);
    }

    private IActionResult JsonError(string message) =>
        new JsonResult(new { error = message }) { StatusCode = 200 };

    private static readonly JsonSerializerOptions JsonBodyOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task<T?> ReadJsonBodyAsync<T>(CancellationToken ct) =>
        await JsonSerializer.DeserializeAsync<T>(Request.Body, JsonBodyOptions, ct);

}

/// <summary>
/// Holds <see cref="ComputePrefill"/> — the server-side prefill priority chain
/// (Boundary judgment call 1: stays here, never duplicated in JS).
/// </summary>
public static class ReviewRowModel
{
    /// <summary>
    /// Pure prefill computation — no URL or HTTP context needed. Applies the priority chain:
    /// user-resolved fields first, AI suggestions for Pending lines as fallback. Only uses
    /// <paramref name="unitIdByCode"/> (label → Guid) and <paramref name="productNameById"/>
    /// (Guid → name).
    ///
    /// <para>Unit priority: user-resolved > receipt-parsed label > (no-receipt-unit only) product default.
    /// Expiry: user-resolved > (Pending + matched + DefaultDueDays) today+N > null.</para>
    /// </summary>
    public static (Guid? ProductId, string? ProductName, decimal? Qty, Guid? UnitId, Guid? LocationId, decimal? Price, DateOnly? Expiry) ComputePrefill(
        ReviewLineView line,
        IReadOnlyDictionary<string, Guid> unitIdByCode,
        IReadOnlyDictionary<Guid, string> productNameById,
        IReadOnlyDictionary<Guid, Guid?> productDefaultLocationById,
        IReadOnlyDictionary<Guid, Guid>? productDefaultUnitById = null,
        IReadOnlyDictionary<Guid, int?>? productDefaultDueDaysById = null,
        DateOnly? today = null)
    {
        var isPending = line.Status == LineStatus.Pending;

        Guid? prefillProductId = line.IsNewProduct ? null
            : line.ProductId
              ?? (isPending && line.SuggestedProductId is { } sugPid && productNameById.ContainsKey(sugPid)
                  ? sugPid : (Guid?)null);

        string? prefillProductName = line.IsNewProduct ? null
            : prefillProductId is { } ppid && productNameById.TryGetValue(ppid, out var ppname)
                ? ppname
                : (isPending ? line.SuggestedProductName : null);

        var prefillQty = line.Quantity ?? (isPending ? line.SuggestedQuantity : null);

        var hasReceiptUnit = isPending && line.SuggestedUnitLabel is not null;

        Guid? prefillUnitId = line.UnitId
            ?? (isPending && line.SuggestedUnitLabel is { } lbl && unitIdByCode.TryGetValue(lbl, out var sugUid)
                ? sugUid
                : (isPending && !hasReceiptUnit && prefillProductId is { } unitPid
                   && productDefaultUnitById is not null
                   && productDefaultUnitById.TryGetValue(unitPid, out var defUid)
                    ? defUid
                    : (Guid?)null));

        Guid? prefillLocationId = line.LocationId
            ?? (isPending && prefillProductId is { } locPid && productDefaultLocationById.TryGetValue(locPid, out var defLoc)
                ? defLoc
                : (Guid?)null);

        var prefillPrice = line.Price ?? (isPending ? line.SuggestedPrice : null);

        DateOnly? prefillExpiry = line.ExpiryDate
            ?? (isPending && prefillProductId is { } expPid
                && productDefaultDueDaysById is not null
                && productDefaultDueDaysById.TryGetValue(expPid, out var dueDays)
                && dueDays is { } n
                && today is { } t
                ? t.AddDays(n)
                : (DateOnly?)null);

        return (prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillLocationId, prefillPrice, prefillExpiry);
    }

}
