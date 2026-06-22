using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Pantry.TakeStock;

/// <summary>
/// "No location" section for the Take Stock flow (P4-8, J7). Shows tracked products that
/// have active stock but no Catalog default location assigned, with a required location picker
/// per row. Saving:
/// <list type="bullet">
/// <item>Always calls <see cref="ITakeStockCatalogWriter.SetDefaultLocationAsync"/> to assign the
/// chosen location as the product's default (TS-9), so it is placed for future walks.</item>
/// <item>When <c>countedValue &gt; 0</c>, also runs <see cref="RecordCountCommand"/> to write an
/// opening-balance <see cref="StockReason.Correction"/> lot in the chosen Location (C8).</item>
/// <item>When <c>countedValue == 0</c>, only files the default location (pure file-this-product, J7
/// edge case — no lot written).</item>
/// </list>
/// On success the client removes the saved row from Alpine state; no full page reload needed.
/// </summary>
[Authorize]
public sealed class NoLocationModel(
    ITakeStockReader reader,
    ITakeStockCatalogWriter catalogWriter,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant) : PageModel
{
    // ── Read model ────────────────────────────────────────────────────────────

    public IReadOnlyList<TakeStockNoLocationRow> Rows { get; private set; } = [];

    /// <summary>Location options rendered in each row's picker.</summary>
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];

    /// <summary>JSON initialiser for the Alpine working set — one entry per row, keyed by product id.</summary>
    public string AlpineRowsJson { get; private set; } = "{}";

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task OnGetAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    // ── POST (Save) ───────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts a JSON body
    /// <c>{ items: [{ productId, locationId, countedValue, countedUnitId }] }</c>
    /// posted by the Alpine save function. For each item:
    /// <list type="bullet">
    /// <item>Always calls <see cref="ITakeStockCatalogWriter.SetDefaultLocationAsync"/> (TS-9).</item>
    /// <item>When <c>countedValue &gt; 0</c>, also runs <see cref="RecordCountCommand"/> to write the
    /// opening-balance Correction lot.</item>
    /// </list>
    /// Returns a per-item result vector for partial-success display (same pattern as Walk/Save — TS-6).
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
        var results = new List<object>(payload.Items.Count);

        foreach (var item in payload.Items)
        {
            if (item.LocationId == Guid.Empty)
            {
                results.Add(new
                {
                    productId = item.ProductId,
                    isSuccess = false,
                    error = "A location is required."
                });
                continue;
            }

            try
            {
                // Step 1 — always file the default location (TS-9, J7).
                await catalogWriter.SetDefaultLocationAsync(item.ProductId, item.LocationId, ct);

                // Step 2 — record the opening-balance Correction when count > 0 (C8).
                if (item.CountedValue > 0m)
                {
                    if (item.CountedUnitId == Guid.Empty)
                    {
                        results.Add(new
                        {
                            productId = item.ProductId,
                            isSuccess = false,
                            error = "A unit is required to record a count."
                        });
                        continue;
                    }

                    var cmd = new RecordCountCommand(
                        item.ProductId,
                        item.LocationId,
                        item.CountedValue,
                        item.CountedUnitId,
                        StockReason.Correction,
                        userId,
                        stocks,
                        conversions,
                        clock,
                        tenant);

                    var countResult = await cmd.ExecuteAsync(ct);
                    if (countResult.IsFailure)
                    {
                        results.Add(new
                        {
                            productId = item.ProductId,
                            isSuccess = false,
                            error = countResult.Error.Description
                        });
                        continue;
                    }
                }

                results.Add(new { productId = item.ProductId, isSuccess = true, error = (string?)null });
            }
            catch (InvalidOperationException ex)
            {
                // Catalog rejection (unknown location, etc.)
                results.Add(new { productId = item.ProductId, isSuccess = false, error = ex.Message });
            }
        }

        return new JsonResult(new { results }, JsonOptions);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await reader.ListNoLocationRowsAsync(ct);

        // Build location options for the picker.
        var locations = await reader.ListLocationsAsync(ct);
        LocationOptions = locations
            .Select(l => new SelectListItem(l.LocationName, l.LocationId.ToString()))
            .ToList();

        // TakeStockNoLocationRow now carries DisplayUnitId directly (C10 additive change);
        // the per-code unit lookup and its IUnitRepository dependency were removed.
        AlpineRowsJson = BuildAlpineRowsJson(Rows);
    }

    private static string BuildAlpineRowsJson(IReadOnlyList<TakeStockNoLocationRow> rows)
    {
        var dict = rows.ToDictionary(
            r => r.ProductId.ToString(),
            r => new AlpineRow(
                r.RecordedQuantity,
                r.RecordedQuantity,
                r.DisplayUnitCode,
                r.DisplayUnitId,
                LocationId: Guid.Empty,
                Dirty: false,
                Failed: false,
                FailMsg: null));

        return JsonSerializer.Serialize(dict, JsonOptions);
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private sealed record AlpineRow(
        [property: JsonPropertyName("recorded")]   decimal  Recorded,
        [property: JsonPropertyName("counted")]    decimal  Counted,
        [property: JsonPropertyName("unitCode")]   string   UnitCode,
        [property: JsonPropertyName("unitId")]     Guid     UnitId,
        [property: JsonPropertyName("locationId")] Guid     LocationId,
        [property: JsonPropertyName("dirty")]      bool     Dirty,
        [property: JsonPropertyName("failed")]     bool     Failed,
        [property: JsonPropertyName("failMsg")]    string?  FailMsg);

    internal sealed class SaveRequest
    {
        public List<SaveItem>? Items { get; set; }
    }

    internal sealed class SaveItem
    {
        public Guid    ProductId    { get; set; }
        public Guid    LocationId   { get; set; }
        public decimal CountedValue { get; set; }
        public Guid    CountedUnitId { get; set; }
    }
}
