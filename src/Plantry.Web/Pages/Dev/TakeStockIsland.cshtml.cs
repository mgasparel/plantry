using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Plantry.Web.Pages.Dev;

/// <summary>
/// Dev-only harness for the Take Stock reactive island (ADR-020 proof, bead plantry-2zvm).
/// Gated to Development by DevPagesGateMiddleware, like the rest of <c>Pages/Dev</c>.
///
/// Mirrors the real Walk page's contract without a database: <see cref="RowsJson"/> hydrates the
/// island the same way <c>WalkModel.AlpineRowsJson</c> does (plus the product name the island needs
/// because it renders the whole row), and <see cref="OnPostDevSaveAsync"/> echoes the exact
/// <c>{ results: [...] }</c> shape <c>WalkModel.OnPostSaveAsync</c> returns — so wiring the island to
/// production later is just pointing <c>saveUrl</c> at the real handler. To exercise the partial-failure
/// reconciliation path, any item with a missing unit is failed (the real server's "unit required" error).
/// </summary>
public sealed class TakeStockIslandModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Hydration payload — a handful of representative rows, one with multiple supported units.</summary>
    public string RowsJson { get; private set; } = "[]";

    public void OnGet()
    {
        var rows = new object[]
        {
            new { productId = "11111111-1111-1111-1111-111111111111", productName = "Whole milk",       recorded = 2m,  unitCode = "L",    unitId = "aaaa1111-0000-0000-0000-000000000001", supportedUnits = new[] { new { unitId = "aaaa1111-0000-0000-0000-000000000001", code = "L" }, new { unitId = "aaaa1111-0000-0000-0000-000000000002", code = "ml" } } },
            new { productId = "22222222-2222-2222-2222-222222222222", productName = "Free-range eggs",   recorded = 12m, unitCode = "each", unitId = "bbbb2222-0000-0000-0000-000000000001", supportedUnits = Array.Empty<object>() },
            new { productId = "33333333-3333-3333-3333-333333333333", productName = "Cheddar cheese",    recorded = 400m, unitCode = "g",   unitId = "cccc3333-0000-0000-0000-000000000001", supportedUnits = Array.Empty<object>() },
            new { productId = "44444444-4444-4444-4444-444444444444", productName = "Penne pasta",       recorded = 1m,  unitCode = "kg",   unitId = "dddd4444-0000-0000-0000-000000000001", supportedUnits = Array.Empty<object>() },
        };
        RowsJson = JsonSerializer.Serialize(rows, JsonOptions);
    }

    /// <summary>
    /// Echoes the production Save contract: accepts <c>{ items: [{ productId, countedValue, countedUnitId, reason }] }</c>
    /// and returns <c>{ results: [{ productId, isSuccess, error? }] }</c>. Fails any item whose unit is missing
    /// (matches the real handler's per-row "unit required" failure) so the island's reconciliation path is exercised.
    /// </summary>
    public async Task<IActionResult> OnPostDevSaveAsync(CancellationToken ct = default)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        DevSaveRequest? payload;
        try { payload = JsonSerializer.Deserialize<DevSaveRequest>(body, JsonOptions); }
        catch { return BadRequest(new { error = "Invalid request body." }); }

        var results = (payload?.Items ?? []).Select(i => string.IsNullOrWhiteSpace(i.CountedUnitId) || i.CountedUnitId == "00000000-0000-0000-0000-000000000000"
            ? new { productId = i.ProductId, isSuccess = false, error = (string?)"Unit is required — please select a unit before saving." }
            : new { productId = i.ProductId, isSuccess = true, error = (string?)null });

        return new JsonResult(new { results });
    }

    private sealed class DevSaveRequest
    {
        public List<DevSaveItem>? Items { get; set; }
    }

    private sealed class DevSaveItem
    {
        public string ProductId { get; set; } = "";
        public decimal CountedValue { get; set; }
        public string? CountedUnitId { get; set; }
        public string? Reason { get; set; }
    }
}
