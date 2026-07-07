using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// The real <see cref="IFlyerSource"/> over Flipp's unofficial, unauthenticated flyers API (D1 — the one
/// fragile external seam). <b>All Flipp-shape knowledge is contained here</b>; the domain only ever sees
/// <see cref="RawDeal"/> / <see cref="DirectoryMerchant"/>. Swapped in for the P5-2
/// <c>StubFlyerSourceAdapter</c> in production; the interface it implements is <b>owned by P5-2</b> and is
/// not redefined here.
/// <para>
/// Follows the Intake untrusted-source pattern (<c>GeminiReceiptParser</c>) exactly:
/// <list type="bullet">
///   <item><b>Soft-fail</b> — every live call is wrapped in try/catch; a network/HTTP error, empty or
///   malformed payload, or missing flyer degrades to an error-carrying <see cref="FlyerPullResult"/>
///   (directory search degrades to an empty list) and <b>never throws into the caller</b> (ADR-007).</item>
///   <item><b>Pure, fixture-testable mapping</b> — <see cref="MapFlyer"/> / <see cref="MapDirectory"/> /
///   <see cref="ParsePrice"/> are static and take raw JSON, so they are unit-tested against recorded
///   payloads with no live call.</item>
///   <item><b>Observability</b> — each call is wrapped in a <see cref="FlyerTelemetry"/> span; no postal
///   code, secret, or PII is logged or tagged.</item>
/// </list>
/// </para>
/// </summary>
public sealed class FlyerSource : IFlyerSource
{
    private readonly HttpClient _http;
    private readonly FlippOptions _options;
    private readonly ILogger<FlyerSource> _logger;

    public FlyerSource(HttpClient http, IOptions<FlippOptions> options, ILogger<FlyerSource> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DirectoryMerchant>> SearchDirectoryAsync(
        string postalCode, string? nameQuery, CancellationToken ct = default)
    {
        // Flipp's feed is postal-code-scoped; a blank postal code has no meaning (deals.md DECISION).
        if (string.IsNullOrWhiteSpace(postalCode))
            return [];

        using var activity = FlyerTelemetry.ActivitySource.StartActivity("flyer_directory_search");

        try
        {
            var dataJson = await GetDataAsync(postalCode, ct);
            var merchants = MapDirectory(dataJson, nameQuery);
            _logger.LogInformation(
                "Flipp directory search returned {MerchantCount} merchant(s).", merchants.Count);
            return merchants;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Directory search has no error channel (the port returns a list) — degrade to empty so the
            // §7e search UI simply shows no results rather than surfacing an exception.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Flipp directory search failed; returning no results.");
            return [];
        }
    }

    public async Task<FlyerPullResult> PullFlyerAsync(
        string externalRef, string postalCode, CancellationToken ct = default)
    {
        using var activity = FlyerTelemetry.ActivitySource.StartActivity("flyer_pull");
        // externalRef is a merchant slug (e.g. "flipp-metro"), not household location — safe to tag.
        activity?.SetTag("deals.store_ref", externalRef);

        if (string.IsNullOrWhiteSpace(postalCode))
        {
            const string msg = "A postal code is required to pull a Flipp flyer.";
            activity?.SetStatus(ActivityStatusCode.Error, msg);
            _logger.LogWarning("Flipp flyer pull for store {StoreRef} had no postal code.", externalRef);
            return FlyerPullResult.Failed(msg);
        }

        try
        {
            var dataJson = await GetDataAsync(postalCode, ct);

            var flyerJson = FindFlyerJson(dataJson, externalRef);
            if (flyerJson is null)
            {
                var msg = $"No active Flipp flyer for store '{externalRef}'.";
                activity?.SetStatus(ActivityStatusCode.Error, msg);
                _logger.LogWarning(
                    "Flipp flyer pull found no active flyer for store {StoreRef}.", externalRef);
                return FlyerPullResult.Failed(msg, dataJson);
            }

            var flyerId = ReadFlyerIdFromJson(flyerJson);
            var itemsJson = flyerId is null ? "[]" : await GetFlyerItemsAsync(flyerId, ct);

            var result = MapFlyer(flyerJson, itemsJson);

            if (result.HasError)
            {
                activity?.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
                _logger.LogWarning(
                    "Flipp flyer pull mapping failed for store {StoreRef}: {Reason}.",
                    externalRef, result.ErrorMessage);
            }
            else
            {
                _logger.LogInformation(
                    "Flipp flyer pull for store {StoreRef} (flyer {FlyerExternalId}) returned {DealCount} deal(s); filtered {FilteredCount} marketing row(s).",
                    externalRef, result.FlyerExternalId, result.Deals.Count, result.FilteredItemCount);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Flipp flyer pull failed for store {StoreRef}.", externalRef);
            return FlyerPullResult.Failed($"Flyer pull failed: {ex.Message}");
        }
    }

    // ── live HTTP (kept thin; all shaping is in the pure mappers below) ──────────────────────────────

    private async Task<string> GetDataAsync(string postalCode, CancellationToken ct)
    {
        var pc = postalCode.Replace(" ", string.Empty).ToUpperInvariant();
        var url =
            $"data?locale={Uri.EscapeDataString(_options.Locale)}&postal_code={Uri.EscapeDataString(pc)}&sid={NewSid()}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> GetFlyerItemsAsync(string flyerId, CancellationToken ct)
    {
        var url =
            $"flyers/{Uri.EscapeDataString(flyerId)}/flyer_items?locale={Uri.EscapeDataString(_options.Locale)}&sid={NewSid()}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // A random 16-digit session id, as Flipp's API expects. Carries no authentication meaning; generated
    // fresh per call.
    private static string NewSid()
    {
        var sb = new StringBuilder(16);
        sb.Append(Random.Shared.Next(1, 10)); // no leading zero → always 16 digits
        for (var i = 1; i < 16; i++)
            sb.Append(Random.Shared.Next(0, 10));
        return sb.ToString();
    }

    // ── pure, fixture-testable mapping ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a Flipp <c>/data</c> payload into the distinct merchants with active flyers, optionally
    /// filtered by a case-insensitive <paramref name="nameQuery"/> substring. Each merchant's
    /// <see cref="DirectoryMerchant.ExternalRef"/> is a stable <c>flipp-{slug}</c> derived from the
    /// merchant name (a merchant may have &gt;1 active flyer — deduped by ref). Pure and defensive:
    /// unexpected shapes yield an empty list, never a throw.
    /// </summary>
    internal static IReadOnlyList<DirectoryMerchant> MapDirectory(string dataJson, string? nameQuery)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return [];

        JsonDocument doc;
        try { doc = JsonDocument.Parse(dataJson); }
        catch (JsonException) { return []; }

        using (doc)
        {
            var query = string.IsNullOrWhiteSpace(nameQuery) ? null : nameQuery.Trim();
            var byRef = new Dictionary<string, DirectoryMerchant>(StringComparer.Ordinal);

            foreach (var flyer in EnumerateFlyers(doc.RootElement))
            {
                var name = ReadMerchantName(flyer);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (query is not null && !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                var externalRef = MerchantRef(name);
                byRef.TryAdd(externalRef, new DirectoryMerchant(externalRef, name.Trim()));
            }

            return byRef.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Maps one Flipp flyer's metadata (<paramref name="flyerJson"/>, from <c>/data</c>) plus its items
    /// (<paramref name="itemsJson"/>, from <c>/flyers/{id}/flyer_items</c>) into a
    /// <see cref="FlyerPullResult"/>: the <see cref="RawDeal"/>s, the <c>flyer_external_id</c>, and the
    /// <see cref="ValidityWindow"/>. <paramref name="itemsJson"/> is preserved verbatim as
    /// <see cref="FlyerPullResult.RawContent"/> — the content-hash <em>input</em>, hashed downstream (P5-6).
    /// Any empty/malformed payload, missing flyer id, or missing/invalid window soft-fails to an
    /// error-carrying result — <b>never throws</b> (ADR-007). Pure; unit-tested against recorded fixtures.
    /// </summary>
    internal static FlyerPullResult MapFlyer(string flyerJson, string itemsJson)
    {
        if (string.IsNullOrWhiteSpace(flyerJson))
            return FlyerPullResult.Failed("Flipp returned an empty flyer payload.", itemsJson ?? string.Empty);

        var rawContent = itemsJson ?? string.Empty;

        JsonElement flyer;
        JsonDocument flyerDoc;
        try { flyerDoc = JsonDocument.Parse(flyerJson); }
        catch (JsonException ex) { return FlyerPullResult.Failed($"Flipp flyer payload was unparseable: {ex.Message}", rawContent); }

        using (flyerDoc)
        {
            flyer = flyerDoc.RootElement;

            var externalId = ReadFlyerId(flyer);
            var from = ReadDate(flyer, "valid_from") ?? ReadDate(flyer, "start_date");
            var to = ReadDate(flyer, "valid_to") ?? ReadDate(flyer, "end_date") ?? ReadDate(flyer, "expires_at");

            if (externalId is null || from is null || to is null)
                return FlyerPullResult.Failed(
                    "Flipp flyer payload was missing a flyer id or validity window.", rawContent);

            var windowResult = ValidityWindow.Create(from.Value, to.Value);
            if (windowResult.IsFailure)
                return FlyerPullResult.Failed(
                    $"Flipp flyer had an invalid validity window: {windowResult.Error.Description}", rawContent);
            var window = windowResult.Value;

            var (deals, filteredCount) = MapItems(rawContent, window);

            return new FlyerPullResult(externalId, window, rawContent, deals, FilteredItemCount: filteredCount);
        }
    }

    // Returns the usable deals plus a count of rows dropped as non-product marketing decoration (see
    // IsMarketingRow) — the latter is surfaced on the pull log line so the matcher-cost saving is visible.
    private static (IReadOnlyList<RawDeal> Deals, int FilteredCount) MapItems(string itemsJson, ValidityWindow window)
    {
        if (string.IsNullOrWhiteSpace(itemsJson))
            return ([], 0);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(itemsJson); }
        catch (JsonException) { return ([], 0); } // MapFlyer already succeeded on the flyer; bad items → no deals

        using (doc)
        {
            var deals = new List<RawDeal>();
            var filtered = 0;
            foreach (var raw in EnumerateItems(doc.RootElement))
            {
                var name = ReadString(raw, "name") ?? ReadString(raw, "product_name");
                if (string.IsNullOrWhiteSpace(name))
                    continue; // an item with no advertised name is unusable

                var brand = ReadString(raw, "brand") ?? ReadString(raw, "brand_name");
                var size = ReadString(raw, "size") ?? ReadString(raw, "pack_size");
                var (price, quantity, saleStory) = ParsePrice(raw);

                if (IsMarketingRow(price, brand, size, saleStory))
                {
                    // Flyer chrome, not a deal — drop it at the ACL boundary so it never reaches the domain,
                    // the AI matcher, or the household's Deals page (ADR-007: Flipp-shape junk stays here).
                    filtered++;
                    continue;
                }

                deals.Add(new RawDeal(
                    RawName: name.Trim(),
                    Brand: Clean(brand),
                    Size: Clean(size),
                    Price: price,
                    Quantity: quantity,
                    UnitId: null, // deferred to P5-6 normalization — the fragile adapter stays catalog-free
                    SaleStory: Clean(saleStory),
                    Window: window));
            }
            return (deals, filtered);
        }
    }

    // A non-product marketing/decoration row (e.g. Flipp's $0 "PRICE DROP", "ALWAYS LOW PRICE",
    // "VALUE THAT MAKES YOU GO GAGA") carries an advertised name but no product substance: no usable price,
    // no brand, no size, and no promo/sale story. Such rows are flyer chrome — dropping them here (a) saves
    // an AI-matcher completion each, (b) keeps a bogus Pending deal off the household's Deals page, and
    // (c) avoids polluting match memory with a negative for a non-product. Deliberately conservative: ANY
    // one of a usable price, a brand, a size, or a sale story keeps the row. We prefer false negatives (a
    // stray junk row a human can reject) over false positives (a silently dropped real deal). Multi-buy
    // promos ("2 for $5") always carry a sale story, so they survive even when their unit price parses to 0.
    private static bool IsMarketingRow(decimal price, string? brand, string? size, string? saleStory) =>
        price <= 0m
        && string.IsNullOrWhiteSpace(brand)
        && string.IsNullOrWhiteSpace(size)
        && string.IsNullOrWhiteSpace(saleStory);

    // Field names vary across Flipp flyer types; try both spellings and derive a per-unit-ish shape.
    // Returns (Price, Quantity, SaleStory): RawDeal.Price is the advertised price for RawDeal.Quantity
    // units. "2 for $5" → (5, 2, "2 for $5"); a plain "$3.99" → (3.99, null, null); unknowable → (0, null,
    // saleStory) with the raw promo text preserved for downstream (P5-6) normalization.
    private static readonly Regex MultiBuyPattern = new(
        @"(\d+)\s*(?:for|/)\s*\$?\s*(\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static (decimal Price, decimal? Quantity, string? SaleStory) ParsePrice(JsonElement raw)
    {
        var saleStory = ReadString(raw, "sale_story") ?? ReadString(raw, "description");
        var plain = ReadDecimal(raw, "current_price") ?? ReadDecimal(raw, "price");

        // A usable plain price wins: it is the advertised price for a single advertised unit.
        if (plain is > 0m)
            return (plain.Value, null, saleStory);

        // Otherwise try to derive from a multi-buy promo ("2 for $5"): price is the total for N units.
        if (!string.IsNullOrWhiteSpace(saleStory))
        {
            var m = MultiBuyPattern.Match(saleStory);
            if (m.Success
                && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty)
                && qty > 0
                && decimal.TryParse(m.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var total))
            {
                return (total, qty, saleStory);
            }
        }

        // Price unknowable from the payload — keep the deal, preserve the promo text, leave price 0.
        return (0m, null, saleStory);
    }

    // ── JSON shape helpers (defensive, mirroring the POC's .get() fallbacks) ─────────────────────────

    // The Flipp /data response is either a bare array of flyers or an object with a flyers/data key.
    private static IEnumerable<JsonElement> EnumerateFlyers(JsonElement root) =>
        EnumerateArrayUnder(root, "flyers", "data");

    // The flyer_items response is either a bare array or an object with a flyer_items/items/data key.
    private static IEnumerable<JsonElement> EnumerateItems(JsonElement root) =>
        EnumerateArrayUnder(root, "flyer_items", "items", "data");

    private static IEnumerable<JsonElement> EnumerateArrayUnder(JsonElement root, params string[] keys)
    {
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            array = default;
            var found = false;
            foreach (var key in keys)
            {
                if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
                {
                    array = el;
                    found = true;
                    break;
                }
            }
            if (!found)
                yield break;
        }
        else
        {
            yield break;
        }

        foreach (var el in array.EnumerateArray())
            if (el.ValueKind == JsonValueKind.Object)
                yield return el;
    }

    // Finds the raw JSON of the first active flyer for a store ref, re-slugging each flyer's merchant name
    // to compare against the ref the directory search handed back. Null when no flyer matches.
    private static string? FindFlyerJson(string dataJson, string externalRef)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(dataJson); }
        catch (JsonException) { return null; }

        using (doc)
        {
            foreach (var flyer in EnumerateFlyers(doc.RootElement))
            {
                var name = ReadMerchantName(flyer);
                if (!string.IsNullOrWhiteSpace(name) && MerchantRef(name) == externalRef)
                    return flyer.GetRawText();
            }
            return null;
        }
    }

    private static string? ReadMerchantName(JsonElement flyer) =>
        ReadString(flyer, "merchant_name") ?? ReadString(flyer, "merchant") ?? ReadString(flyer, "store_name");

    private static string? ReadFlyerId(JsonElement flyer) =>
        ReadIdString(flyer, "id") ?? ReadIdString(flyer, "flyer_id");

    private static string? ReadFlyerIdFromJson(string flyerJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(flyerJson);
            return ReadFlyerId(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A stable ref for a merchant, derived from its name: "No Frills" → "flipp-no-frills". Mirrors the
    // P5-2 stub's shape so directory results feed back into PullFlyer without a Flipp merchant_id.
    private static readonly Regex NonSlugChars = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    internal static string MerchantRef(string merchantName)
    {
        var slug = NonSlugChars.Replace(merchantName.Trim().ToLowerInvariant(), "-").Trim('-');
        return "flipp-" + slug;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // An id that may arrive as a JSON string or number.
    private static string? ReadIdString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(p.GetString()) ? null : p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        };
    }

    // A decimal that may arrive as a JSON number or a numeric string ("$3.99", "3.99").
    private static decimal? ReadDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
            return null;
        switch (p.ValueKind)
        {
            case JsonValueKind.Number:
                return p.TryGetDecimal(out var n) ? n : null;
            case JsonValueKind.String:
                var s = p.GetString();
                if (string.IsNullOrWhiteSpace(s))
                    return null;
                s = s.Replace("$", string.Empty).Replace(",", string.Empty).Trim();
                return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
            default:
                return null;
        }
    }

    private static DateOnly? ReadDate(JsonElement el, string name)
    {
        var s = ReadString(el, name);
        if (string.IsNullOrWhiteSpace(s))
            return null;
        // Flipp dates are ISO-8601: either a bare date or a full timestamp. Parse invariantly.
        if (DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return DateOnly.FromDateTime(dto.UtcDateTime);
        return null;
    }
}
