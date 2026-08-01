using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Shared;

namespace Plantry.Web.Pages.Intake;

/// <summary>
/// Manual intake form (plantry-45ba.3) — <c>/Intake/Manual</c>. The user types a purchase they made
/// (a whole shop, a single item, or a lost receipt where only some prices are remembered) with no
/// receipt to scan. Server-rendered + Alpine for the repeating line rows (no review deck, no Preact
/// island — there is nothing AI-suggested to review). A single submit posts straight to
/// <see cref="LogManualPurchaseCommand"/> and lands on the committed session's detail page.
///
/// <para>The line editor reuses the shared <see cref="ProductSearchCreateSheetViewModel"/> sheet
/// (<c>Shared/_ProductSearchCreateSheet</c>) already shared across Intake review / Take Stock /
/// Recipes: pick an existing product (prefilling unit/location/expiry from its Catalog defaults) or
/// create a new one in the same pass. Location, price, and expiry — fields the shared sheet doesn't
/// carry — are injected via its <see cref="ProductSearchCreateSheetViewModel.ExtraFieldsPartial"/>
/// slot (<c>Intake/_ManualLineExtraFields</c>).</para>
///
/// <para>Rows are mirrored to the server via hidden inputs bound to <c>Input.Lines[n]</c> (the same
/// dual-render technique <c>Recipes/Edit.cshtml</c> uses), so a validation bounce — either this
/// page's own line-shape guard or <see cref="LogManualPurchaseCommand"/>'s domain validation — returns
/// here with every typed line re-seeded into the Alpine <c>rows</c> array via <see cref="RowsJson"/>
/// (the acceptance requirement: losing a half-typed shop to a validation bounce is the main failure
/// mode worth guarding against).</para>
/// </summary>
[Authorize]
public sealed class ManualModel(
    IImportSessionRepository sessions,
    IReviewReferenceDataProvider referenceData,
    ICreateProductPort createProduct,
    IAddStockPort addStock,
    IRecordPricePort recordPrice,
    IEnsurePurchaseStorePort ensureStore,
    ISeedConversionPort seedConversion,
    IClock clock,
    ITenantContext tenant,
    DisplayCurrencyAccessor displayCurrency,
    ILogger<CommitSessionCommand> commitLogger,
    ILogger<LogManualPurchaseCommand> logger) : PageModel
{
    private static readonly JsonSerializerOptions RowsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [BindProperty]
    public ManualPurchaseFormInput Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];

    /// <summary>Household stores, serialised as <c>[{ id, name }]</c> for the client-filtered store picker.</summary>
    public string StoresJson { get; private set; } = "[]";

    /// <summary>
    /// Initial state for Alpine's <c>rows</c> array. Empty on a fresh GET; re-seeded from
    /// <see cref="Input"/> on a validation bounce so the user's typed lines survive the round-trip.
    /// </summary>
    public string RowsJson { get; private set; } = "[]";

    /// <summary>
    /// Initial state for Alpine's store-picker fields (<c>storeQuery</c>/<c>selectedStoreId</c>). Empty
    /// on a fresh GET; re-seeded from <see cref="Input"/> on a validation bounce — a typed store name is
    /// part of the same half-typed shop the acceptance criterion says must survive a bounce, not just the
    /// line rows.
    /// </summary>
    public string HeaderJson { get; private set; } = """{"storeQuery":"","selectedStoreId":""}""";

    /// <summary>The household's display-currency symbol (plantry-2x6e.3) — the line-row summary prefixes
    /// each price with this rather than a hardcoded dollar sign, matching every other money-rendering
    /// surface (see <see cref="MoneyDisplay"/>'s class remarks on why no currency map lives in JS).
    /// Defaults to the USD symbol via <see cref="MoneyDisplay.Symbol"/> — never a bare string literal,
    /// which the source-scanning guard (<c>MoneyFormattingGuardTests</c>) forbids everywhere but
    /// <c>MoneyDisplay</c> itself.</summary>
    public string CurrencySymbol { get; private set; } = MoneyDisplay.Symbol("USD");

    public async Task OnGetAsync(CancellationToken ct)
    {
        Input.PurchaseDate = clock.ToLocalDate(clock.UtcNow);
        await LoadReferenceDataAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var reference = await LoadReferenceDataAsync(ct);

        var valid = true;
        if (!ModelState.IsValid)
            valid = false;
        if (Input.PurchaseDate == default)
        {
            ModelState.AddModelError(string.Empty, "Enter the purchase date.");
            valid = false;
        }
        if (Input.Lines.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Enter at least one line.");
            valid = false;
        }

        for (var i = 0; i < Input.Lines.Count; i++)
        {
            var line = Input.Lines[i];
            if (line.Quantity is null)
            {
                ModelState.AddModelError(string.Empty, $"Line {i + 1}: enter a quantity.");
                valid = false;
            }
            if (line.UnitId is null)
            {
                ModelState.AddModelError(string.Empty, $"Line {i + 1}: choose a unit.");
                valid = false;
            }
            if (line.LocationId is null)
            {
                ModelState.AddModelError(string.Empty, $"Line {i + 1}: choose a location.");
                valid = false;
            }
        }

        if (!valid)
        {
            RowsJson = BuildRowsJson(Input.Lines, reference);
            HeaderJson = BuildHeaderJson(Input);
            return Page();
        }

        var lines = Input.Lines
            .Select(l => new ManualPurchaseLineInput(
                l.ProductId, l.NewProductName, l.NewProductCategoryId,
                l.Quantity!.Value, l.UnitId!.Value, l.LocationId!.Value, l.Price, l.ExpiryDate))
            .ToList();

        var command = new LogManualPurchaseCommand(
            CurrentUserId, Input.MerchantText, Input.SelectedStoreId, Input.PurchaseDate, lines,
            sessions, createProduct, addStock, recordPrice, ensureStore, referenceData, seedConversion,
            clock, tenant, commitLogger, logger);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            RowsJson = BuildRowsJson(Input.Lines, reference);
            HeaderJson = BuildHeaderJson(Input);
            return Page();
        }

        return Redirect(Url.Page("/Intake/Session", new { id = result.Value.Value })!);
    }

    // ── GET (product search — the shared add/edit sheet, htmx) ──────────────────

    /// <summary>
    /// Returns <c>&lt;li role="option"&gt;</c> markup for the line editor's product search. Ranks the
    /// household's stock-eligible products (the only ones a manual line can resolve to) with the shared
    /// <see cref="ProductNameMatcher"/>, matching every other product-search sheet in the app. Each hit
    /// carries the product's server-computed prefill (unit / location / expiry) as extra <c>data-*</c>
    /// attributes so the client-side pick handler can seed the line without re-deriving the chain itself —
    /// the ticket's explicit instruction to reuse <see cref="ReviewPrefill"/> rather than recompute
    /// today+DefaultDueDays client-side (which would use the browser's clock/timezone, not the app's).
    /// </summary>
    public async Task<IActionResult> OnGetSearchProductsAsync(string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Content("", "text/html");

        var reference = await referenceData.GetAsync(ct);
        var byId = reference.Products.ToDictionary(p => p.Id);
        var hits = ProductNameMatcher.Rank(reference.Products.Select(p => (p.Id, p.Name)), q.Trim());
        var lookups = ReviewPrefill.BuildLookups(reference);
        var today = clock.ToLocalDate(clock.UtcNow);

        var html = string.Join("", hits.Select((h, i) =>
        {
            var p = byId[h.Id];
            var label = ProductNameMatcher.RankLabel(h.Score, isTopHit: i == 0);

            // A synthetic Pending line naming this product and nothing else — ComputePrefill's priority
            // chain then falls straight through to the product's own defaults (no user-resolved value,
            // no AI suggestion, both intentionally absent for a typed manual line).
            var line = new ReviewLineView(
                LineId: Guid.Empty, LineNo: 0, ReceiptText: p.Name, SuggestedConfidence: SuggestedConfidence.None,
                Status: LineStatus.Pending, ProductId: p.Id, SkuId: null, Quantity: null, UnitId: null,
                LocationId: null, ExpiryDate: null, Price: null, IsNewProduct: false, NewProductName: null,
                NewProductCategoryId: null, SuggestedProductId: null, SuggestedProductName: null,
                SuggestedQuantity: null, SuggestedUnitLabel: null, SuggestedPrice: null);
            var prefill = ReviewPrefill.ComputePrefill(line, lookups, today);

            return ProductSearchOptionRenderer.RenderPickProductOption(
                p.Id.ToString(), p.Name, label,
                [
                    new ProductOptionField("default-unit", prefill.UnitId?.ToString() ?? "", "defaultUnit"),
                    new ProductOptionField("default-location", prefill.LocationId?.ToString() ?? "", "defaultLocation"),
                    new ProductOptionField("default-expiry", prefill.Expiry?.ToString("yyyy-MM-dd") ?? "", "defaultExpiry"),
                ]);
        }));
        return Content(html, "text/html");
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<ReviewReferenceData> LoadReferenceDataAsync(CancellationToken ct)
    {
        var reference = await referenceData.GetAsync(ct);

        UnitOptions = BuildUnitOptions(reference.Units);
        CategoryOptions = reference.Categories
            .Select(c => new SelectListItem(c.Name, c.Id.ToString()))
            .ToList();
        LocationOptions = reference.Locations
            .Select(l => new SelectListItem(l.Name, l.Id.ToString()))
            .ToList();
        StoresJson = JsonSerializer.Serialize(
            reference.Stores.Select(s => new { id = s.Id.ToString(), name = s.Name }).ToList(),
            RowsJsonOptions);
        CurrencySymbol = MoneyDisplay.Symbol(await displayCurrency.GetAsync(ct));

        return reference;
    }

    /// <summary>
    /// Groups <see cref="ReviewUnitOption"/> into a dimension-grouped &lt;optgroup&gt; select list —
    /// a <see cref="ReviewUnitOption"/>-typed sibling of <see cref="UnitSelectListBuilder.Build{T}"/>,
    /// which can't be reused directly here: it's generic over Catalog's own <c>Dimension</c> enum, while
    /// this page reads reference data through <see cref="IReviewReferenceDataProvider"/>'s Intake-local
    /// <see cref="ReviewUnitDimension"/> mirror (kept deliberately Catalog-free, Gate 2). Same
    /// Mass → Volume → Count, then-Code ordering; same shared-<see cref="SelectListGroup"/>-instance
    /// requirement the doc comment on <see cref="UnitSelectListBuilder.GroupFor"/> explains.
    /// </summary>
    private static List<SelectListItem> BuildUnitOptions(IReadOnlyList<ReviewUnitOption> units)
    {
        var groups = new Dictionary<ReviewUnitDimension, SelectListGroup>();
        return units
            .OrderBy(u => u.Dimension)
            .ThenBy(u => u.Code, StringComparer.OrdinalIgnoreCase)
            .Select(u =>
            {
                if (!groups.TryGetValue(u.Dimension, out var group))
                {
                    group = new SelectListGroup { Name = u.Dimension.ToString() };
                    groups[u.Dimension] = group;
                }
                return new SelectListItem(u.Name, u.Id.ToString()) { Group = group };
            })
            .ToList();
    }

    /// <summary>
    /// Re-seeds Alpine's <c>rows</c> array from the posted lines on a validation bounce, resolving each
    /// existing-product line's display name from <paramref name="reference"/> (only the id is posted
    /// back). A new-product line has no id to resolve — its typed name round-trips as-is.
    /// </summary>
    private static string BuildRowsJson(IReadOnlyList<ManualLineFormInput> lines, ReviewReferenceData reference)
    {
        var productNames = reference.Products.ToDictionary(p => p.Id, p => p.Name);
        var rows = lines.Select((l, i) => new
        {
            _id = i,
            productId = l.ProductId?.ToString() ?? "",
            productName = l.ProductId is { } pid && productNames.TryGetValue(pid, out var name) ? name : "",
            newStapleName = l.NewProductName,
            newStapleCategoryId = l.NewProductCategoryId?.ToString() ?? "",
            qty = l.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "",
            unitId = l.UnitId?.ToString() ?? "",
            locationId = l.LocationId?.ToString() ?? "",
            price = l.Price?.ToString(CultureInfo.InvariantCulture) ?? "",
            expiryDate = l.ExpiryDate?.ToString("yyyy-MM-dd") ?? "",
        }).ToList();
        return JsonSerializer.Serialize(rows, RowsJsonOptions);
    }

    /// <summary>
    /// Re-seeds Alpine's store-picker fields from the posted header on a validation bounce — a typed
    /// store name (or picked store id) is part of the same half-typed shop <see cref="BuildRowsJson"/>
    /// re-seeds for the lines; losing it on a bounce would be the same failure mode applied to the header.
    /// </summary>
    private static string BuildHeaderJson(ManualPurchaseFormInput input) =>
        JsonSerializer.Serialize(
            new
            {
                storeQuery = input.MerchantText ?? "",
                selectedStoreId = input.SelectedStoreId?.ToString() ?? "",
            },
            RowsJsonOptions);
}

/// <summary>Bound form model for the whole manual-purchase submit.</summary>
public sealed class ManualPurchaseFormInput
{
    /// <summary>Typed store name — flows straight through to <see cref="LogManualPurchaseCommand"/>'s
    /// find-or-create path when <see cref="SelectedStoreId"/> is null.</summary>
    public string? MerchantText { get; set; }

    /// <summary>An explicitly picked existing store id, when the user selected one from the list
    /// rather than typing a new name.</summary>
    public Guid? SelectedStoreId { get; set; }

    public DateOnly PurchaseDate { get; set; }

    public List<ManualLineFormInput> Lines { get; set; } = [];
}

/// <summary>Bound form model for one repeating line row.</summary>
public sealed class ManualLineFormInput
{
    public Guid? ProductId { get; set; }
    public string? NewProductName { get; set; }
    public Guid? NewProductCategoryId { get; set; }
    public decimal? Quantity { get; set; }
    public Guid? UnitId { get; set; }
    public Guid? LocationId { get; set; }
    public decimal? Price { get; set; }
    public DateOnly? ExpiryDate { get; set; }
}
