using System.Net;
using System.Text.Json;
using Plantry.Intake.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Snapshot / state tests for the intake review island hydration JSON.
///
/// Prior to plantry-2zvm.3, this file tested HTML fragment snapshots of the server-rendered
/// _ReviewRow partial (one per line state). Those tests are retired: the review form now runs on
/// a Preact island that renders client-side from a hydration JSON payload; there are no
/// server-rendered row elements to snapshot.
///
/// What replaces them: assertions that the hydration JSON emitted on GET carries the correct
/// per-line state and server-computed prefill values for each fixture line. The prefill priority
/// chain (ComputePrefill) is still executed server-side (Boundary judgment call 1, ADR-020 §3),
/// so verifying the hydrated values is the correct pinning point.
/// </summary>
public sealed class ReviewFragmentSnapshotTests(ReviewFragmentFactory factory)
    : IClassFixture<ReviewFragmentFactory>
{
    private async Task<JsonDocument> GetHydrationAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Intake/Review/{factory.SessionAId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var match = System.Text.RegularExpressions.Regex.Match(
            body, "<script type=\"application/json\" id=\"review-island-data\">(.*?)</script>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, "Hydration script element not found on review page.");
        return JsonDocument.Parse(match.Groups[1].Value.Trim());
    }

    private JsonElement FindLine(JsonElement linesArray, string receiptText)
    {
        foreach (var item in linesArray.EnumerateArray())
        {
            var text = item.GetProperty("line").GetProperty("receiptText").GetString();
            if (text == receiptText) return item;
        }
        throw new InvalidOperationException($"Line with receiptText '{receiptText}' not found in hydration.");
    }

    // ── Line states in hydration ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Row_matched_has_pending_status_and_prefill()
    {
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        var line = FindLine(lines, "WHOLE MILK 2L");

        Assert.Equal("Pending", line.GetProperty("line").GetProperty("status").GetString());
        Assert.Equal("High", line.GetProperty("line").GetProperty("confidence").GetString());

        // Server-computed prefill: AI suggestion → Milk product, 2 L, Fridge
        var prefill = line.GetProperty("prefill");
        Assert.Equal(ReviewSessionFixture.MilkProductId.ToString(), prefill.GetProperty("productId").GetString());
        Assert.Equal(2m, prefill.GetProperty("quantity").GetDecimal());
        Assert.Equal(ReviewSessionFixture.LitreUnitId.ToString(), prefill.GetProperty("unitId").GetString());
        Assert.Equal(ReviewSessionFixture.FridgeLocationId.ToString(), prefill.GetProperty("locationId").GetString());
    }

    [Fact]
    public async Task Row_unmatched_has_pending_none_and_empty_prefill()
    {
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        var line = FindLine(lines, "MYSTERY ITEM XZ");

        Assert.Equal("Pending", line.GetProperty("line").GetProperty("status").GetString());
        Assert.Equal("None", line.GetProperty("line").GetProperty("confidence").GetString());

        var prefill = line.GetProperty("prefill");
        Assert.True(prefill.GetProperty("productId").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || string.IsNullOrEmpty(prefill.GetProperty("productId").GetString()));
    }

    [Fact]
    public async Task Row_new_product_has_confirmed_and_isNewProduct_true()
    {
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        var line = FindLine(lines, "ARTISAN SOURDOUGH");

        Assert.Equal("Confirmed", line.GetProperty("line").GetProperty("status").GetString());
        Assert.True(line.GetProperty("line").GetProperty("isNewProduct").GetBoolean());
        Assert.Equal("Sourdough Loaf", line.GetProperty("line").GetProperty("newProductName").GetString());
    }

    [Fact]
    public async Task Row_dismissed_has_dismissed_status()
    {
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        var line = FindLine(lines, "PLASTIC BAG");

        Assert.Equal("Dismissed", line.GetProperty("line").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Row_committed_has_committed_status()
    {
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        var line = FindLine(lines, "BUTTER 250G");

        Assert.Equal("Committed", line.GetProperty("line").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Row_confirmed_existing_has_confirmed_status_and_product()
    {
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        var line = FindLine(lines, "FREE RANGE EGGS");

        Assert.Equal("Confirmed", line.GetProperty("line").GetProperty("status").GetString());
        Assert.Equal(ReviewSessionFixture.EggsProductId.ToString(),
            line.GetProperty("line").GetProperty("productId").GetString());
    }

    // ── "Did you mean" alternatives ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Row_with_alternatives_has_alternatives_in_hydration()
    {
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        var line = FindLine(lines, "CHEDDAR BLK 400G");

        Assert.True(line.TryGetProperty("alternatives", out var alts), "Alternatives must be present.");
        Assert.True(alts.ValueKind == JsonValueKind.Array, "Alternatives must be an array.");
        Assert.True(alts.GetArrayLength() >= 2, "Must have 2+ alternatives.");
    }

    // ── Prefill: product-default expiry chain ────────────────────────────────────────────────

    [Fact]
    public async Task Prefill_expiry_computed_from_default_due_days()
    {
        // Milk has DefaultDueDays=7; the fixed clock is SnapshotDate (2026-06-15).
        // Expected expiry = 2026-06-22.
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        var line = FindLine(lines, "WHOLE MILK 2L");
        var prefill = line.GetProperty("prefill");

        Assert.True(prefill.TryGetProperty("expiry", out var expiry), "Prefill must include expiry.");
        Assert.Equal("2026-06-22", expiry.GetString());
    }

    // ── All lines present ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task All_eight_lines_present_in_hydration()
    {
        using var doc = await GetHydrationAsync();
        var lines = doc.RootElement.GetProperty("lines");
        Assert.Equal(8, lines.GetArrayLength());
    }

    // ── Products include defaults for island product-reselection (Boundary judgment call 2) ──

    [Fact]
    public async Task Products_in_hydration_include_defaults_for_island_reselection()
    {
        using var doc = await GetHydrationAsync();
        var products = doc.RootElement.GetProperty("products");

        var milk = products.EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("id").GetString() == ReviewSessionFixture.MilkProductId.ToString());
        Assert.True(milk.ValueKind != JsonValueKind.Undefined, "Milk must be in hydration products.");

        var defaults = milk.GetProperty("defaults");
        Assert.Equal(ReviewSessionFixture.LitreUnitId.ToString(), defaults.GetProperty("unitId").GetString());
        Assert.Equal(ReviewSessionFixture.FridgeLocationId.ToString(), defaults.GetProperty("locationId").GetString());
        // Expiry default: today (2026-06-15) + 7 = 2026-06-22
        Assert.Equal("2026-06-22", defaults.GetProperty("expiry").GetString());
    }
}
