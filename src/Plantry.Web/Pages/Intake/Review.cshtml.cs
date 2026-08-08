using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Market.Application;
using Plantry.Market.Domain;
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
/// server-side via <see cref="ReviewPrefill.ComputePrefill"/> (the priority chain stays server-side,
/// per ADR-020 §3 / Boundary judgment call 1).</para>
///
/// <para>POST endpoints return JSON (ADR-015 amendment: island data endpoints return JSON):
///   SaveLine    → { status, isNewProduct, newProductName, productId, productName, price } | { error }
///   DismissLine → { status } | { error }
///   RestoreLine → { status } | { error }
///   ReopenLine  → { status } | { error }
///   CorrectHeader → { merchantText, selectedStoreId, purchaseDate, purchaseTime } | { error }
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
    IEnsurePurchaseStorePort ensureStore,
    ISeedConversionPort seedConversion,
    IPriceObservationRepository priceRepository,
    IUnitPriceCalculator priceCalculator,
    IClock clock,
    ITenantContext tenant,
    DisplayCurrencyAccessor displayCurrency,
    ILogger<CommitSessionCommand> commitLogger,
    ILogger<DiscardSessionCommand> discardLogger,
    ILogger<DismissLineCommand> dismissLogger,
    ILogger<ResolveLineCommand> resolveLogger,
    ILogger<ConfirmLineAsNewCommand> confirmAsNewLogger,
    ILogger<RestoreLineCommand> restoreLogger,
    ILogger<ReopenLineCommand> reopenLogger,
    ILogger<ConfirmLinesCommand> confirmLinesLogger,
    ILogger<CorrectSessionHeaderCommand> correctHeaderLogger) : PageModel
{
    /// <summary>Pure hydration-JSON projector (plantry-uk4u). Stateless and dependency-free, so it is
    /// held as a shared instance rather than threaded through the (already 18-dep) primary constructor.</summary>
    private static readonly IntakeReviewHydrationBuilder hydrationBuilder = new();

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
        public Guid? StagedProductId { get; set; }
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

        // Resolve the household display-currency symbol once (plantry-2x6e.3) so the island's money formatters
        // prefix the same glyph the server renders with — sourced from MoneyDisplay.Symbol, no currency map in JS.
        var currencySymbol = MoneyDisplay.Symbol(await displayCurrency.GetAsync(ct));
        var (priceDeltas, dealHits) = await ComputePriceStatsAsync(Session.Lines, ct);
        var trailingAverageBasket = tenant.HouseholdId is { } householdGuid
            ? await new GetTrailingAverageBasketQuery(sessions).ExecuteAsync(HouseholdId.From(householdGuid), ct)
            : null;
        var hydration = hydrationBuilder.Build(
            Session, Today, clock.UtcNow, clock.Zone, BuildHandlerUrls(), currencySymbol,
            priceDeltas, trailingAverageBasket, dealHits);
        IslandHydrationJson = JsonSerializer.Serialize(hydration, IntakeHydrationJson.Options);
        return Page();
    }

    private static readonly Dictionary<Guid, decimal> EmptyUnitPrices = [];
    private static readonly HashSet<Guid> EmptyDealHitLineIds = [];

    /// <summary>
    /// Computes both intake-review trip-context stats that key off each line's own normalized unit price
    /// (plantry-bb7p): the per-line unit-price delta vs the product's last purchase ("▲ 12% vs last time")
    /// and the "you got the deal" marker (once plantry-j9q4's deal-hit stamp landed, its confirmed-deal
    /// query is reused here for the review-time preview). Both are impure by necessity — a cross-context
    /// read into Market pricing plus the unit-normalization port — so they stay here rather than in the
    /// pure <see cref="IntakeReviewHydrationBuilder"/>; the delta's pure combination logic itself lives in
    /// <see cref="IntakeLinePriceDeltas"/>.
    ///
    /// <para>Only Confirmed lines with a resolved product, positive quantity, unit and price are
    /// considered — the same fields <see cref="CommitSessionCommand"/> requires to record a purchase.
    /// Each line's own unit price is normalized exactly once and shared between the delta and deal-hit
    /// passes (a second <see cref="IUnitPriceCalculator.TryNormalizeAsync"/> loop would double the
    /// per-line unit lookups for no reason). <paramref name="lines"/> lets a caller scope the work to a
    /// single just-saved line (<see cref="OnPostSaveLineAsync"/>) instead of the whole session — resolving
    /// one row must not cost the whole receipt's worth of price/unit lookups.</para>
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, decimal> Deltas, IReadOnlySet<Guid> DealHits)> ComputePriceStatsAsync(
        IReadOnlyList<ReviewLineView> lines, CancellationToken ct)
    {
        var confirmed = lines
            .Where(l => l.Status == LineStatus.Confirmed && l.ProductId is not null &&
                        l.Quantity is > 0m && l.UnitId is not null && l.Price is not null)
            .ToList();
        if (confirmed.Count == 0)
            return (IntakeLinePriceDeltas.Compute(lines, EmptyUnitPrices, EmptyUnitPrices), EmptyDealHitLineIds);

        var productIds = confirmed.Select(l => l.ProductId!.Value).Distinct().ToList();
        var lastObservations = await priceRepository.LatestForProductsAsync(productIds, ct);
        var lastUnitPriceByProductId = lastObservations
            .Where(kv => kv.Value.UnitPrice is > 0m)
            .ToDictionary(kv => kv.Key, kv => kv.Value.UnitPrice!.Value);

        var currentUnitPriceByLineId = new Dictionary<Guid, decimal>();
        foreach (var line in confirmed)
        {
            var normalized = await priceCalculator.TryNormalizeAsync(
                line.Price!.Value, line.Quantity!.Value, line.UnitId!.Value, ct);
            if (normalized is { } price && price > 0m)
                currentUnitPriceByLineId[line.LineId] = price;
        }

        var deltas = IntakeLinePriceDeltas.Compute(lines, currentUnitPriceByLineId, lastUnitPriceByProductId);
        var dealHits = await ComputeDealHitsAsync(confirmed, currentUnitPriceByLineId, ct);
        return (deltas, dealHits);
    }

    /// <summary>
    /// "You got the deal" preview (plantry-bb7p, deferred half of the injection appendix — now unblocked
    /// since plantry-j9q4's confirmed-deal query landed on this epic branch): for each Confirmed line whose
    /// own unit price normalized, checks whether an active Confirmed deal at the session's store covers
    /// the (possibly corrected) purchase date at-or-below that price, using the same qualification
    /// predicate and tolerance <see cref="DealHitMatcher"/> uses at commit time (batched into one round trip
    /// via <see cref="IPriceObservationRepository.ProductIdsWithQualifyingDealAsync"/>) — so a line that
    /// previews as a hit here is guaranteed to actually stamp as one at commit. The store is the explicit
    /// CorrectHeader pick when there is one, else a read-only name match of the AI-parsed merchant text
    /// against the household's existing stores (see the store-resolution comment in the body). Returns
    /// empty when neither resolves a store, or the session has no purchase date yet — an unknown-merchant
    /// or undated session can't know which store's deals apply.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ComputeDealHitsAsync(
        IReadOnlyList<ReviewLineView> confirmedLines,
        IReadOnlyDictionary<Guid, decimal> currentUnitPriceByLineId,
        CancellationToken ct)
    {
        // Store resolution mirrors commit's, read-only: an explicit CorrectHeader store pick wins, else the
        // AI-parsed merchant text is matched against the household's stores using the same
        // whitespace-normalize + ordinal compare EnsureStoreByNameCommand applies at commit
        // (StoreCommands.NormalizeName) — so a receipt whose merchant text names a known store previews its
        // deal hits without the user ever opening the header editor. Deliberately NOT the find-or-CREATE
        // commit path: a GET must never mint a Store row, so an unknown merchant simply previews no hits
        // (commit will still stamp them after it creates the store).
        var storeId = Session.SelectedStoreId
            ?? (string.IsNullOrWhiteSpace(Session.MerchantText)
                ? null
                : Session.ReferenceData.Stores
                    .FirstOrDefault(s => string.Equals(
                        System.Text.RegularExpressions.Regex.Replace(s.Name.Trim(), @"\s+", " "),
                        System.Text.RegularExpressions.Regex.Replace(Session.MerchantText!.Trim(), @"\s+", " "),
                        StringComparison.Ordinal))?.Id);
        if (storeId is not { } resolvedStoreId || Session.PurchaseDate is not { } purchaseDate)
            return EmptyDealHitLineIds;

        // One batch read for the whole receipt (same convention as the LatestForProductsAsync call above —
        // a resolved 40-line receipt must not cost 40 sequential deal queries to paint one page). When two
        // lines resolve to the same product (two pack sizes, routine), the DEAREST line's unit price is the
        // one checked: if it qualifies, every cheaper sibling line qualifies a fortiori, and if it doesn't,
        // no line previews as a hit — keeping the guarantee that a previewed hit always stamps at commit
        // (a cheaper sibling that would have stamped merely misses the preview chip, never the stamp).
        var candidateLines = confirmedLines
            .Where(l => l.ProductId is not null && currentUnitPriceByLineId.ContainsKey(l.LineId))
            .ToList();
        if (candidateLines.Count == 0)
            return EmptyDealHitLineIds;

        var unitPriceByProductId = candidateLines
            .GroupBy(l => l.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(l => currentUnitPriceByLineId[l.LineId]));

        var qualifyingProductIds = await priceRepository.ProductIdsWithQualifyingDealAsync(
            unitPriceByProductId, resolvedStoreId, purchaseDate, DealHitMatcher.DealHitTolerance, ct);

        return candidateLines
            .Where(l => qualifyingProductIds.Contains(l.ProductId!.Value))
            .Select(l => l.LineId)
            .ToHashSet();
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
                sessions, tenant, confirmAsNewLogger,
                stagedProductId: edit.StagedProductId).ExecuteAsync(ct);
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

        // Price-delta / deal-hit chips (plantry-bb7p): SaveLine is the row-resolve path the deck/drawer
        // flow uses, so it — not just the initial page GET — must carry the just-confirmed line's stats,
        // or the chips would only ever appear after a full page reload. Scoped to just this one line (not
        // the whole session) so resolving a single row costs one price query + one unit lookup, not one
        // per Confirmed line in the receipt.
        var (priceDeltas, dealHits) = await ComputePriceStatsAsync([updated], ct);
        var priceDeltaPercent = priceDeltas.TryGetValue(updated.LineId, out var delta) ? (decimal?)delta : null;

        return new JsonResult(new
        {
            status = updated.Status.ToString(),
            isNewProduct = updated.IsNewProduct,
            newProductName = updated.NewProductName,
            productId = updated.ProductId?.ToString(),
            productName = updated.ProductId is { } pid
                ? Session.ReferenceData.Products.FirstOrDefault(p => p.Id == pid)?.Name
                : null,
            stagedProductId = updated.StagedProductId?.ToString(),
            stagedProduct = updated.StagedProductId is { } stagedId &&
                (Session.StagedProducts ?? []).FirstOrDefault(p => p.Id == stagedId) is { } staged
                    ? new
                    {
                        id = staged.Id.ToString(),
                        name = staged.Name,
                        categoryId = staged.CategoryId.ToString(),
                        defaultUnitId = staged.DefaultUnitId.ToString(),
                    }
                    : null,
            price = updated.Price ?? updated.SuggestedPrice,
            priceDeltaPercent,
            dealHit = dealHits.Contains(updated.LineId),
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

    /// <summary>Reopens a confirmed line back to Pending (undo-of-a-resolve and the "Wrong product — review
    /// again" rematch of the exceptions-first flow, plantry-v0wl), so the AI prefill re-applies and the line
    /// can be resolved afresh. Returns { status: "Pending" } like RestoreLine.</summary>
    public async Task<IActionResult> OnPostReopenLineAsync([FromQuery] Guid lineId, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct);
        if (loaded is { } failure)
            return failure;

        var id = ImportLineId.From(lineId);
        var result = await new ReopenLineCommand(
            ImportSessionId.From(Id), id, sessions, tenant, reopenLogger).ExecuteAsync(ct);

        if (result.IsFailure)
            return JsonError(result.Error.Description);

        return new JsonResult(new { status = "Pending", error = (string?)null });
    }

    /// <summary>Request body for <see cref="OnPostConfirmLinesAsync"/> — the island sends ONLY line ids;
    /// the AI-suggested values are re-derived server-side and never echoed back (Gate 5).</summary>
    public sealed class ConfirmLinesInput
    {
        public List<Guid> LineIds { get; set; } = [];
    }

    /// <summary>Bulk-confirms a set of Pending, High-confidence, complete-prefill lines from their
    /// server-side prefill values (the deck-flow checklist enabler, plantry-kr9h). Atomic: any non-qualifying
    /// id fails the whole call and confirms nothing. Returns the confirmed line ids + their new status as
    /// JSON, mirroring the per-line handlers' shape. Id is route-bound (from
    /// /Intake/Review/{id:guid}?handler=ConfirmLines).</summary>
    public async Task<IActionResult> OnPostConfirmLinesAsync(CancellationToken ct)
    {
        ConfirmLinesInput input;
        try
        {
            input = await ReadJsonBodyAsync<ConfirmLinesInput>(ct) ?? new ConfirmLinesInput();
        }
        catch
        {
            return JsonError("Invalid request body.");
        }

        var lineIds = input.LineIds.Select(ImportLineId.From).ToList();

        var result = await new ConfirmLinesCommand(
            ImportSessionId.From(Id), lineIds, sessions, referenceData, clock, tenant, confirmLinesLogger)
            .ExecuteAsync(ct);

        if (result.IsFailure)
            return JsonError(result.Error.Description);

        // Price-delta / deal-hit chips (plantry-bb7p): like SaveLine, bulk-confirm flips lines to Confirmed
        // without a page reload, so its response must carry each confirmed line's stats too — otherwise the
        // same screen would show chips on a line resolved one-at-a-time but not on one confirmed via the
        // checklist, until the next full page load. Scoped to just the confirmed lines and batched inside
        // ComputePriceStatsAsync (one price read + one deal read for the whole set).
        await LoadAsync(ct);
        var confirmedIds = result.Value.ToHashSet();
        var confirmedViews = Session.Lines.Where(l => confirmedIds.Contains(l.LineId)).ToList();
        var (priceDeltas, dealHits) = await ComputePriceStatsAsync(confirmedViews, ct);

        return new JsonResult(new
        {
            confirmedLineIds = result.Value.Select(id => id.ToString()).ToList(),
            lines = confirmedViews.Select(l => new
            {
                lineId = l.LineId.ToString(),
                priceDeltaPercent = priceDeltas.TryGetValue(l.LineId, out var delta) ? (decimal?)delta : null,
                dealHit = dealHits.Contains(l.LineId),
            }).ToList(),
            status = LineStatus.Confirmed.ToString(),
            error = (string?)null,
        });
    }

    /// <summary>Request body for <see cref="OnPostCorrectHeaderAsync"/> — the review header correction
    /// (plantry-yobz). Date/time arrive as raw strings (ISO <c>yyyy-MM-dd</c> / 24h <c>HH:mm</c>) and are
    /// parsed here; a blank string clears the field. <c>SelectedStoreId</c> is the picked catalog store
    /// (null = the merchant-text find-or-create path).</summary>
    public sealed class CorrectHeaderInput
    {
        public string? MerchantText { get; set; }
        public Guid? SelectedStoreId { get; set; }
        public string? PurchaseDate { get; set; }
        public string? PurchaseTime { get; set; }
    }

    /// <summary>Applies a user correction to the parsed receipt header — store pick / merchant name, purchase
    /// date, purchase time (plantry-yobz). The (possibly corrected) purchase date threads through commit to
    /// back-date the stock lot; a picked store id resolves the purchase store directly. Returns the resolved
    /// header echo as JSON so the island re-locks its display from server truth. Id is route-bound.</summary>
    public async Task<IActionResult> OnPostCorrectHeaderAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is null)
            return JsonError("Unauthorized.");

        CorrectHeaderInput input;
        try
        {
            input = await ReadJsonBodyAsync<CorrectHeaderInput>(ct) ?? new CorrectHeaderInput();
        }
        catch
        {
            return JsonError("Invalid request body.");
        }

        DateOnly? purchaseDate = null;
        if (!string.IsNullOrWhiteSpace(input.PurchaseDate))
        {
            if (!DateOnly.TryParse(input.PurchaseDate, CultureInfo.InvariantCulture, out var parsedDate))
                return JsonError("Enter a valid purchase date.");
            purchaseDate = parsedDate;
        }

        TimeOnly? purchaseTime = null;
        if (!string.IsNullOrWhiteSpace(input.PurchaseTime))
        {
            if (!TimeOnly.TryParse(input.PurchaseTime, CultureInfo.InvariantCulture, out var parsedTime))
                return JsonError("Enter a valid purchase time.");
            purchaseTime = parsedTime;
        }

        var result = await new CorrectSessionHeaderCommand(
            ImportSessionId.From(Id), input.MerchantText, input.SelectedStoreId, purchaseDate, purchaseTime,
            sessions, referenceData, clock, tenant, correctHeaderLogger).ExecuteAsync(ct);

        if (result.IsFailure)
            return JsonError(result.Error.Description);

        // Reload and echo the resolved header so the island re-locks from server truth (not its own draft).
        await LoadAsync(ct);

        // Per-line stat refresh (plantry-bb7p): a header correction mutates the two inputs the deal-hit
        // marker derives from (store + purchase date), so already-rendered chips can become wrong, not just
        // missing — correcting from a store with a deal to one without must clear the marker immediately.
        // Same response rule SaveLine/ConfirmLines follow: a mutation that changes a line's stats carries
        // them. Every line is projected (not only Confirmed ones) so a line that ceases to qualify is
        // actively cleared rather than left stale.
        var (priceDeltas, dealHits) = await ComputePriceStatsAsync(Session.Lines, ct);

        return new JsonResult(new
        {
            merchantText = Session.MerchantText,
            selectedStoreId = Session.SelectedStoreId?.ToString(),
            purchaseDate = Session.PurchaseDate?.ToString("ddd MMM d, yyyy", CultureInfo.CurrentCulture),
            purchaseTime = Session.PurchaseTime?.ToString("h:mm tt", CultureInfo.CurrentCulture),
            purchaseDateRaw = Session.PurchaseDate?.ToString("yyyy-MM-dd"),
            purchaseTimeRaw = Session.PurchaseTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            lines = Session.Lines.Select(l => new
            {
                lineId = l.LineId.ToString(),
                priceDeltaPercent = priceDeltas.TryGetValue(l.LineId, out var delta) ? (decimal?)delta : null,
                dealHit = dealHits.Contains(l.LineId),
            }).ToList(),
            error = (string?)null,
        });
    }

    // ── Commit / discard — return redirect target so the island can navigate ────────

    public async Task<IActionResult> OnPostCommitAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is null)
            return JsonError("Unauthorized.");

        var result = await new CommitSessionCommand(
            ImportSessionId.From(Id), sessions, createProduct, addStock, recordPrice, ensureStore,
            referenceData, seedConversion, clock, tenant, commitLogger)
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

    /// <summary>Computes the seven row-action handler URLs for the hydration payload. <c>Url.Page(...)</c>
    /// needs PageModel context, so this stays page-side and the values are handed to the pure
    /// <see cref="IntakeReviewHydrationBuilder"/> (plantry-uk4u).</summary>
    private ReviewHandlerUrls BuildHandlerUrls() => new(
        Commit: Url.Page("./Review", "Commit", new { Id })!,
        Discard: Url.Page("./Review", "Discard", new { Id })!,
        SaveLine: Url.Page("./Review", "SaveLine", new { Id })!,
        DismissLine: Url.Page("./Review", "DismissLine", new { Id })!,
        RestoreLine: Url.Page("./Review", "RestoreLine", new { Id })!,
        Reopen: Url.Page("./Review", "ReopenLine", new { Id })!,
        ConfirmLines: Url.Page("./Review", "ConfirmLines", new { Id })!,
        CorrectHeader: Url.Page("./Review", "CorrectHeader", new { Id })!);

    private IActionResult JsonError(string message) =>
        new JsonResult(new { error = message }) { StatusCode = 200 };

    private static readonly JsonSerializerOptions JsonBodyOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task<T?> ReadJsonBodyAsync<T>(CancellationToken ct) =>
        await JsonSerializer.DeserializeAsync<T>(Request.Body, JsonBodyOptions, ct);

}
