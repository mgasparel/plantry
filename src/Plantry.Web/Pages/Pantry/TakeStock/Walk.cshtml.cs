using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Pantry.TakeStock;

/// <summary>
/// Walk page for one Location (P4-4b, J2/J4). Shows the C5 union product rows with count inputs
/// (stepper + display-unit, reason selector), wired to <see cref="SaveCountsCommand"/> on POST.
///
/// Also hosts the lot escape-hatch panel (P4-5, J3): GET /Lots returns a per-(product, location)
/// lot list fragment; POST /SaveLots applies per-lot <see cref="SaveLotAdjustmentsCommand"/>
/// adjustments and returns a per-item result JSON.
///
/// Inline-add (P4-7, J5): GET /SearchProducts returns <c>&lt;li&gt;</c> markup for the shared
/// product-search sheet; POST /AddItem runs <see cref="AddCountedItemCommand"/> (create + opening
/// balance) and returns the new product row as JSON for Alpine to inject into the working set.
///
/// Alpine owns the working set until the user taps Save. Save POSTs a JSON body of dirty rows only;
/// the handler runs <see cref="SaveCountsCommand"/> and returns a per-item result JSON for the
/// client to reflect success/failure on each row without a page reload (C7, TS-6).
/// </summary>
[Authorize]
public sealed class WalkModel(
    ITakeStockReader reader,
    ITakeStockCatalogWriter catalogWriter,
    IUnitRepository unitRepository,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant) : PageModel
{
    // ── Read model ────────────────────────────────────────────────────────────

    [BindProperty(SupportsGet = true)]
    public Guid LocationId { get; set; }

    public string? LocationName { get; private set; }

    public IReadOnlyList<TakeStockLocationProductRow> Rows { get; private set; } = [];

    /// <summary>JSON initialiser for the Alpine working set — one entry per row, keyed by product id.</summary>
    public string AlpineRowsJson { get; private set; } = "{}";

    /// <summary>Unit options for the inline-add sheet's unit selector.</summary>
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task OnGetAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    // ── GET (Product search — inline add sheet, P4-7) ─────────────────────────

    /// <summary>
    /// Returns &lt;li role="option"&gt; markup for the inline-add product search.
    /// Called by htmx on keyup in the shared product-search sheet's input.
    /// Only tracked, non-archived products are returned (same filter as <see cref="ITakeStockReader.SearchProductsAsync"/>).
    /// </summary>
    public async Task<IActionResult> OnGetSearchProductsAsync(string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Content("", "text/html");

        var hits = await reader.SearchProductsAsync(q.Trim(), ct);
        var enc = HtmlEncoder.Default;
        var html = string.Join("", hits.Select(p =>
            $$"""<li role="option" data-value="{{p.ProductId}}" data-track="true" data-default-location="{{p.DefaultLocationId}}" data-default-unit="{{p.DefaultUnitId}}" @click="query = $el.textContent.trim(); open = false; $dispatch('pick-product', {value: $el.dataset.value, name: $el.textContent.trim(), track: 'true', defaultUnitId: $el.dataset.defaultUnit})">{{enc.Encode(p.Name)}}</li>"""));
        return Content(html, "text/html");
    }

    // ── POST (Add item — inline create + opening balance, P4-7) ─────────────────

    /// <summary>
    /// Accepts a JSON body
    /// <c>{ name, defaultUnitId, countedValue, countedUnitId }</c>
    /// posted by the Alpine inline-add sheet's saveSheet() function.
    ///
    /// Runs <see cref="AddCountedItemCommand"/> (create tracked product → RecordCount opening
    /// balance) and returns a JSON response the Alpine client uses to inject the new product row
    /// into the working set:
    /// <c>{ isSuccess, productId, productName, unitCode, unitId, countedValue, error? }</c>.
    ///
    /// The new row is added to Alpine's <c>rows</c> map at the current recorded quantity (the
    /// just-applied opening balance) so the row shows as saved (not dirty) immediately.
    /// </summary>
    public async Task<IActionResult> OnPostAddItemAsync(CancellationToken ct = default)
    {
        using var bodyReader = new StreamReader(Request.Body);
        var bodyJson = await bodyReader.ReadToEndAsync(ct);
        AddItemRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AddItemRequest>(bodyJson, JsonOptions);
        }
        catch
        {
            return BadRequest(new { error = "Invalid request body." });
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Name))
            return BadRequest(new { error = "Name is required." });

        if (tenant.HouseholdId is null)
            return Unauthorized();

        var userId = CurrentUserId;

        var cmd = new AddCountedItemCommand(
            payload.Name.Trim(),
            payload.DefaultUnitId,
            LocationId,
            payload.CountedValue,
            payload.CountedUnitId == Guid.Empty ? payload.DefaultUnitId : payload.CountedUnitId,
            userId,
            catalogWriter,
            stocks,
            conversions,
            clock,
            tenant);

        var result = await cmd.ExecuteAsync(ct);

        if (result.IsFailure)
            return new JsonResult(new { isSuccess = false, error = result.Error.Description });

        var productId = result.Value;

        // Resolve the unit code for the response — the client needs it to display the row.
        var units = await unitRepository.ListAsync(ct);
        var unitCode = units.FirstOrDefault(u => u.Id.Value == payload.DefaultUnitId)?.Code ?? "?";

        return new JsonResult(new
        {
            isSuccess = true,
            productId,
            productName = payload.Name.Trim(),
            unitCode,
            unitId = payload.DefaultUnitId,
            countedValue = payload.CountedValue,
        });
    }

    // ── POST (Save) ───────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts a JSON body <c>{ items: [{ productId, countedValue, countedUnitId, reason }] }</c>
    /// posted by the Alpine save function. Runs <see cref="SaveCountsCommand"/> and returns a
    /// per-item result so the client can reflect partial failures without a page reload (TS-6 / C7).
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct = default)
    {
        using var bodyReader = new StreamReader(Request.Body);
        var bodyJson = await bodyReader.ReadToEndAsync(ct);
        SaveRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SaveRequest>(bodyJson, JsonOptions);
        }
        catch
        {
            return BadRequest(new { error = "Invalid request body." });
        }

        if (payload?.Items is null)
            return BadRequest(new { error = "No items supplied." });

        if (tenant.HouseholdId is null)
            return Unauthorized();

        var userId = CurrentUserId;

        // Defense-in-depth: skip items where countedUnitId is missing, empty string, or Guid.Empty
        // rather than letting System.Text.Json throw (non-parseable string) or the command fail the
        // whole batch. SaveItem.CountedUnitId is typed as string? so that an empty-string payload
        // ("countedUnitId": "") from stale Path A clients deserializes cleanly instead of throwing.
        // The root cause (missing DefaultUnitId in Path A) is fixed in selectProduct() and the
        // search result projection, but this guard defends against any remaining stale clients.
        var invalidItems = new List<SaveItem>();
        var validItems = new List<CountItem>();

        foreach (var i in payload.Items)
        {
            if (!Guid.TryParse(i.CountedUnitId, out var unitId) || unitId == Guid.Empty)
            {
                invalidItems.Add(i);
            }
            else
            {
                validItems.Add(new CountItem(
                    i.ProductId,
                    LocationId,
                    i.CountedValue,
                    unitId,
                    ParseReason(i.Reason)));
            }
        }

        // Build per-row results — invalid items get an inline error, valid items go through the command.
        var perRowResults = new List<object>();

        foreach (var invalid in invalidItems)
        {
            perRowResults.Add(new
            {
                ProductId = invalid.ProductId,
                IsSuccess = false,
                error = "Unit is required — please select a unit before saving.",
            });
        }

        if (validItems.Count > 0)
        {
            var cmd = new SaveCountsCommand(validItems, userId, stocks, conversions, clock, tenant);
            var result = await cmd.ExecuteAsync(ct);

            if (result.IsFailure)
                return StatusCode(500, new { error = result.Error.Description });

            perRowResults.AddRange(result.Value.Select(r => (object)new
            {
                r.ProductId,
                r.IsSuccess,
                error = r.IsSuccess ? null : r.FailureReason?.Description,
            }));
        }

        return new JsonResult(new { results = perRowResults });
    }

    // ── GET (Lots fragment — escape hatch) ────────────────────────────────────

    /// <summary>
    /// Returns the lot-panel fragment for a single (product, location) pair (P4-5, J3).
    /// Called via htmx GET when the user expands a count row's lot list.
    /// Renders <c>_LotPanel</c> partial pre-populated with active lots from
    /// <see cref="ITakeStockReader.ListLotsAsync"/>.
    /// </summary>
    public async Task<IActionResult> OnGetLotsAsync(Guid productId, CancellationToken ct = default)
    {
        var lots = await reader.ListLotsAsync(productId, LocationId, ct);
        return Partial("_LotPanel", new LotPanelModel(productId, LocationId, lots));
    }

    // ── POST (SaveLots — escape-hatch adjustments) ────────────────────────────

    /// <summary>
    /// Accepts a JSON body of per-lot adjustments (P4-5, J3):
    /// <c>{ adjustments: [{ entryId?, amount, unitId, reason, expiryDate? }] }</c>.
    /// Runs <see cref="SaveLotAdjustmentsCommand"/> and returns a per-item result vector.
    /// </summary>
    public async Task<IActionResult> OnPostSaveLotsAsync(Guid productId, CancellationToken ct = default)
    {
        using var bodyReader = new StreamReader(Request.Body);
        var bodyJson = await bodyReader.ReadToEndAsync(ct);
        SaveLotsRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SaveLotsRequest>(bodyJson, JsonOptions);
        }
        catch
        {
            return BadRequest(new { error = "Invalid request body." });
        }

        if (payload?.Adjustments is null)
            return BadRequest(new { error = "No adjustments supplied." });

        if (tenant.HouseholdId is null)
            return Unauthorized();

        var userId = CurrentUserId;
        var adjustments = payload.Adjustments
            .Select(a => new LotAdjustItem(
                a.EntryId,
                a.Amount,
                a.UnitId,
                ParseReason(a.Reason),
                a.ExpiryDate))
            .ToList();

        var cmd = new SaveLotAdjustmentsCommand(
            productId, LocationId, adjustments, userId, stocks, conversions, clock, tenant);
        var result = await cmd.ExecuteAsync(ct);

        if (result.IsFailure)
            return StatusCode(500, new { error = result.Error.Description });

        var outcome = result.Value;
        if (!outcome.IsSuccess && outcome.Results.Count == 0)
            return StatusCode(500, new { error = outcome.FailureReason?.Description });

        var responseItems = outcome.Results.Select(r => new
        {
            r.EntryId,
            r.IsSuccess,
            error = r.IsSuccess ? null : r.FailureReason?.Description,
        });

        return new JsonResult(new { results = responseItems });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken ct)
    {
        var locations = await reader.ListLocationsAsync(ct);
        LocationName = locations.FirstOrDefault(l => l.LocationId == LocationId)?.LocationName;
        Rows = await reader.ListLocationRowsAsync(LocationId, ct);
        AlpineRowsJson = BuildAlpineRowsJson(Rows);

        // Load unit options for the inline-add sheet (P4-7).
        var units = await unitRepository.ListAsync(ct);
        UnitOptions = units
            .OrderBy(u => u.Code, StringComparer.OrdinalIgnoreCase)
            .Select(u => new SelectListItem(u.Code, u.Id.Value.ToString()))
            .ToList();
    }

    private static string BuildAlpineRowsJson(IReadOnlyList<TakeStockLocationProductRow> rows)
    {
        var dict = rows.ToDictionary(
            r => r.ProductId.ToString(),
            r => new AlpineRow(
                r.RecordedQuantity,
                r.RecordedQuantity,
                r.DisplayUnitCode,
                r.DisplayUnitId,
                "Correction",
                false,
                false,
                null,
                r.SupportedUnits?
                    .Select(u => new AlpineUnitOption(u.UnitId, u.Code))
                    .ToList() ?? []));

        return JsonSerializer.Serialize(dict, JsonOptions);
    }

    private static StockReason ParseReason(string? reason) => reason switch
    {
        "Consumed"  => StockReason.Consumed,
        "Discarded" => StockReason.Discarded,
        _           => StockReason.Correction,
    };

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private sealed record AlpineRow(
        [property: JsonPropertyName("recorded")]       decimal                    Recorded,
        [property: JsonPropertyName("counted")]        decimal                    Counted,
        [property: JsonPropertyName("unitCode")]       string                     UnitCode,
        [property: JsonPropertyName("unitId")]         Guid                       UnitId,
        [property: JsonPropertyName("reason")]         string                     Reason,
        [property: JsonPropertyName("dirty")]          bool                       Dirty,
        [property: JsonPropertyName("failed")]         bool                       Failed,
        [property: JsonPropertyName("failMsg")]        string?                    FailMsg,
        [property: JsonPropertyName("supportedUnits")] List<AlpineUnitOption>     SupportedUnits);

    private sealed record AlpineUnitOption(
        [property: JsonPropertyName("unitId")] Guid   UnitId,
        [property: JsonPropertyName("code")]   string Code);

    private sealed class SaveRequest
    {
        public List<SaveItem>? Items { get; set; }
    }

    private sealed class SaveItem
    {
        public Guid    ProductId     { get; set; }
        public decimal CountedValue  { get; set; }
        /// <summary>
        /// Stored as string? so JSON deserialization tolerates an empty string ("") from stale
        /// Path A clients that posted unitId before DefaultUnitId was wired up.
        /// Parsed to Guid (with empty-string → Guid.Empty → per-row error) in OnPostSaveAsync.
        /// </summary>
        public string? CountedUnitId { get; set; }
        public string? Reason        { get; set; }
    }

    private sealed class SaveLotsRequest
    {
        public List<SaveLotAdjust>? Adjustments { get; set; }
    }

    private sealed class SaveLotAdjust
    {
        public Guid?    EntryId    { get; set; }
        public decimal  Amount     { get; set; }
        public Guid     UnitId     { get; set; }
        public string?  Reason     { get; set; }
        public DateOnly? ExpiryDate { get; set; }
    }

    private sealed class AddItemRequest
    {
        /// <summary>Product name typed in the create-new mode of the inline-add sheet.</summary>
        public string? Name           { get; set; }
        /// <summary>Selected default unit for the new product.</summary>
        public Guid   DefaultUnitId   { get; set; }
        /// <summary>Counted quantity for the opening-balance Correction (0 = register only, no stock).</summary>
        public decimal CountedValue   { get; set; }
        /// <summary>Unit for the counted quantity; falls back to DefaultUnitId when empty.</summary>
        public Guid   CountedUnitId   { get; set; }
    }
}

/// <summary>
/// View model for the <c>_LotPanel</c> partial (P4-5, J3).
/// Carries the active lots for one (product, location) pair to render the escape-hatch lot list.
/// </summary>
public sealed record LotPanelModel(
    Guid ProductId,
    Guid LocationId,
    IReadOnlyList<TakeStockLotRow> Lots);
