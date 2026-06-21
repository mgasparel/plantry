using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Pantry.TakeStock;

/// <summary>
/// Walk page for one Location (P4-4b, J2/J4). Shows the C5 union product rows with count inputs
/// (stepper + display-unit, reason selector), wired to <see cref="SaveCountsCommand"/> on POST.
///
/// Alpine owns the working set until the user taps Save. Save POSTs a JSON body of dirty rows only;
/// the handler runs <see cref="SaveCountsCommand"/> and returns a per-item result JSON for the
/// client to reflect success/failure on each row without a page reload (C7, TS-6).
/// </summary>
[Authorize]
public sealed class WalkModel(
    ITakeStockReader reader,
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

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task OnGetAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

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
        var items = payload.Items
            .Select(i => new CountItem(
                i.ProductId,
                LocationId,
                i.CountedValue,
                i.CountedUnitId,
                ParseReason(i.Reason)))
            .ToList();

        var cmd = new SaveCountsCommand(items, userId, stocks, conversions, clock, tenant);
        var result = await cmd.ExecuteAsync(ct);

        if (result.IsFailure)
            return StatusCode(500, new { error = result.Error.Description });

        var responseItems = result.Value.Select(r => new
        {
            r.ProductId,
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
                null));

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
        [property: JsonPropertyName("recorded")]     decimal Recorded,
        [property: JsonPropertyName("counted")]      decimal Counted,
        [property: JsonPropertyName("unitCode")]     string  UnitCode,
        [property: JsonPropertyName("unitId")]       Guid    UnitId,
        [property: JsonPropertyName("reason")]       string  Reason,
        [property: JsonPropertyName("dirty")]        bool    Dirty,
        [property: JsonPropertyName("failed")]       bool    Failed,
        [property: JsonPropertyName("failMsg")]      string? FailMsg);

    private sealed class SaveRequest
    {
        public List<SaveItem>? Items { get; set; }
    }

    private sealed class SaveItem
    {
        public Guid    ProductId     { get; set; }
        public decimal CountedValue  { get; set; }
        public Guid    CountedUnitId { get; set; }
        public string? Reason        { get; set; }
    }
}
