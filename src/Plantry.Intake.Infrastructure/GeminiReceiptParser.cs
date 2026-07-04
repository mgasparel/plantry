using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.Globalization;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;

namespace Plantry.Intake.Infrastructure;

/// <summary>
/// <see cref="IReceiptParser"/> over an OpenAI-compatible <c>ChatClient</c> (OpenRouter + Gemini by
/// default, see <see cref="AiOptions"/>). Collapses the ReceiptPoc two-stage pipeline into a single
/// multimodal call: the receipt image plus the household's catalog hints go in, structured line items
/// with inline product-id suggestions come back.
///
/// The AI is an untrusted external function (ADR-007): output is a proposal, never a write. Any API or
/// parse failure is a <em>soft</em> failure — it returns a <see cref="ReceiptParseResult"/> carrying an
/// <see cref="ReceiptParseResult.ErrorMessage"/> (which the caller maps to <c>MarkParsingFailed</c>) and
/// never throws into the page.
///
/// Observability (Gate 9): each call is wrapped in an <see cref="Activity"/> span
/// (<c>receipt_parse</c>) with <c>ai.model</c>, <c>ai.usage.input_tokens</c>,
/// and <c>ai.usage.output_tokens</c> attributes. Per-line confidence scores are recorded on
/// <see cref="AiTelemetry.ParseConfidence"/>. Failures set the span to
/// <see cref="ActivityStatusCode.Error"/> and emit a <c>LogError</c>. No receipt content, prompt
/// text, or API key is written to any log or span attribute.
/// </summary>
public sealed class GeminiReceiptParser : IReceiptParser
{
    private readonly ChatClient _chat;
    private readonly string _modelId;
    private readonly ILogger<GeminiReceiptParser> _logger;

    public GeminiReceiptParser(
        IOptions<AiOptions> options,
        ILogger<GeminiReceiptParser> logger)
    {
        _logger = logger;
        var ai = options.Value;
        _modelId = ai.Model;
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(ai.BaseUrl) };
        _chat = new OpenAIClient(new ApiKeyCredential(ai.ApiKey), clientOptions)
            .GetChatClient(ai.Model);
    }

    private const string SystemPrompt = """
        You parse a photographed grocery receipt into structured line items and match each to the
        household's product catalog. Return ONLY valid JSON, no markdown.

        Output format:
        {
          "merchant": "store name or null",
          "store_branch": "branch / location / address line printed under the store name, or null",
          "purchase_date": "the receipt date as YYYY-MM-DD, or null",
          "purchase_time": "the receipt time as HH:MM in 24-hour clock, or null",
          "subtotal": 0.00,
          "tax": 0.00,
          "total": 0.00,
          "payment": "the payment/tender line verbatim, e.g. 'VISA ****4471 APPROVED', or null",
          "receipt_number": "the receipt / transaction / invoice number printed on the receipt, or null",
          "lines": [
            {
              "line_no": 1,
              "receipt_text": "the item text as printed",
              "suggested_product_id": "catalog UUID from the list, or null",
              "suggested_product_name": "the matched catalog name, or null",
              "quantity": 1,
              "unit": "kg or lb for weight-priced items, otherwise null",
              "price": 5.49,
              "confidence": "high | low | none",
              "estimated_each_count": null,
              "each_confidence": null,
              "alternatives": [
                { "product_id": "catalog UUID copied verbatim", "product_name": "catalog name", "confidence": 0.72 }
              ]
            }
          ]
        }

        Rules:
        - One line per purchased product. Skip savings lines, subtotals, loyalty points, totals, headers.
        - price = final amount paid for that line after discounts.
        - quantity = weight (kg/lb) for weight-priced items, otherwise the item count.
        - suggested_product_id MUST be copied verbatim from the catalog list below, or null.
        - Weight-priced item tracked by EACH (produce → estimate a count): when a line is priced by
          weight (kg/lb) AND you matched it to a catalog product marked "tracked by: each", the household
          counts this product in whole units, not by weight. Keep quantity+unit as the true weight, and
          ALSO set estimated_each_count to your best estimate of how many units that weight represents
          (e.g. 1.34 lb of bananas ≈ 7), with each_confidence = "high" (confident) or "low" (a rough
          guess). Estimate from typical unit weights for that product.
        - Do NOT estimate a count when the matched product is "tracked by: weight/volume" (deli meat,
          bulk grains, oil) or is unmatched: leave estimated_each_count and each_confidence null. A
          genuinely weight-tracked product stays in its weight unit.
        - When a line is NOT weight-priced, leave estimated_each_count and each_confidence null.
        - confidence: "high" = clear match; "low" = plausible but uncertain; "none" = nothing reasonable
          in the catalog (then suggested_product_id and suggested_product_name are null).
        - alternatives: for EVERY line, list the next-best catalog candidates EXCLUDING whichever product
          you chose as suggested_product_id, best match first, up to 3 entries. Each product_id MUST be
          copied verbatim from the catalog list — do not invent ids or use free-text names without an id.
          Each confidence is a decimal in [0, 1]. Emit fewer than 3 when fewer plausible catalog matches
          exist. Emit an empty array [] when there are no credible alternatives.
        - store_branch, purchase_date, purchase_time, subtotal, tax, total, payment, receipt_number:
          read these from the receipt header/footer for display. Use null (for text) or omit the field
          when it is not printed — never guess. subtotal/tax/total are decimals as printed.
        """;

    public async Task<ReceiptParseResult> ParseAsync(
        byte[] imageBytes,
        string contentType,
        IReadOnlyList<ProductHint> catalogHints,
        CancellationToken ct = default)
    {
        // Gate 9: span wraps the full AI call (latency-sensitive, most likely failure point).
        // Attributes: model id and token usage only — no receipt content or API key.
        using var activity = AiTelemetry.ActivitySource.StartActivity("receipt_parse");
        activity?.SetTag("ai.model", _modelId);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "AI receipt parse starting. Model: {Model}, CatalogHints: {HintCount}.",
            _modelId, catalogHints.Count);

        try
        {
            var userMessage = new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(BuildCatalogBlock(catalogHints)),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), contentType));

            var response = await _chat.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), userMessage],
                cancellationToken: ct);

            var completion = response.Value;
            RecordTokenUsage(activity, completion.Usage);

            var rawText = completion.Content.Count > 0 ? completion.Content[0].Text : null;

            // Gate 9: empty response is a soft failure but must surface as an error span + log.
            if (string.IsNullOrWhiteSpace(rawText))
            {
                activity?.SetStatus(ActivityStatusCode.Error, "AI returned an empty response.");
                _logger.LogError(
                    "AI receipt parse returned an empty response. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                    _modelId, sw.ElapsedMilliseconds);
                return new ReceiptParseResult(null, [], "AI returned an empty response.");
            }

            var result = MapResponse(rawText);

            if (result.HasError)
            {
                // MapResponse soft-failed (malformed JSON etc.) — surface as error span.
                activity?.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
                _logger.LogError(
                    "AI receipt parse response could not be mapped. Model: {Model}, ElapsedMs: {ElapsedMs}. Reason: {Reason}.",
                    _modelId, sw.ElapsedMilliseconds, result.ErrorMessage);
                return result;
            }

            // Record per-line confidence histogram (Gate 9 metric requirement).
            foreach (var line in result.Lines)
                AiTelemetry.ParseConfidence.Record(ConfidenceScore(line.Confidence));

            sw.Stop();
            _logger.LogInformation(
                "AI receipt parse completed. Model: {Model}, Lines: {LineCount}, ElapsedMs: {ElapsedMs}.",
                _modelId, result.Lines.Count, sw.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "AI receipt parse failed with exception. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                _modelId, sw.ElapsedMilliseconds);
            return new ReceiptParseResult(null, [], $"Receipt parsing failed: {ex.Message}");
        }
    }

    private static string BuildCatalogBlock(IReadOnlyList<ProductHint> hints)
    {
        var sb = new StringBuilder("Match against this catalog (id — name — skus — tracking):\n");
        foreach (var h in hints)
        {
            sb.Append('[').Append(h.Id).Append("] ").Append(h.Name);
            if (h.SkuLabels.Count > 0)
                sb.Append(" — ").Append(string.Join(", ", h.SkuLabels));
            // Tracking unit tells the model whether a weight-priced match should also be estimated as an
            // each-count (plantry-1mu): only "tracked by: each" products get an estimated_each_count.
            sb.Append(" — tracked by: ").Append(h.TracksEach ? "each" : "weight/volume");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Maps the model's raw text response into a <see cref="ReceiptParseResult"/>. Strips markdown
    /// fences, parses the JSON, and per-line preserves the raw object JSON in <c>RawJson</c> (the ACL
    /// provenance half). Any malformed/empty content soft-fails to an error result. Extracted as a pure
    /// static method so it is unit-testable against recorded fixtures with no live API call.
    /// </summary>
    internal static ReceiptParseResult MapResponse(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return new ReceiptParseResult(null, [], "AI returned an empty response.");

        try
        {
            using var doc = JsonDocument.Parse(StripFences(rawContent));
            var root = doc.RootElement;

            var merchant = root.TryGetProperty("merchant", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            var lines = new List<ParsedLine>();
            if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var el in linesEl.EnumerateArray())
                {
                    index++;
                    var primaryId = GetGuid(el, "suggested_product_id");
                    lines.Add(new ParsedLine(
                        LineNo: GetInt(el, "line_no") ?? index,
                        ReceiptText: GetString(el, "receipt_text") ?? string.Empty,
                        SuggestedProductName: GetString(el, "suggested_product_name"),
                        SuggestedProductId: primaryId,
                        Quantity: GetDecimal(el, "quantity"),
                        UnitLabel: GetString(el, "unit"),
                        Price: GetDecimal(el, "price"),
                        Confidence: GetString(el, "confidence"),
                        RawJson: el.GetRawText(),
                        Alternatives: MapAlternatives(el, primaryId),
                        EstimatedEachCount: GetPositiveDecimal(el, "estimated_each_count"),
                        EstimatedEachConfidence: GetString(el, "each_confidence")));
                }
            }

            var metadata = new ReceiptMetadata(
                StoreBranch: GetString(root, "store_branch"),
                PurchaseDate: GetDate(root, "purchase_date"),
                PurchaseTime: GetTime(root, "purchase_time"),
                Subtotal: GetDecimal(root, "subtotal"),
                Tax: GetDecimal(root, "tax"),
                Total: GetDecimal(root, "total"),
                PaymentDescriptor: GetString(root, "payment"),
                ReceiptNumber: GetString(root, "receipt_number"));

            return new ReceiptParseResult(merchant, lines, Metadata: metadata);
        }
        catch (JsonException ex)
        {
            return new ReceiptParseResult(null, [], $"AI returned unparseable JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the <c>alternatives</c> array from a line element and enforces the extras-only contract
    /// (ADR-007 — AI is untrusted): entries missing a parseable catalog <c>product_id</c> are dropped;
    /// any id that duplicates the primary <paramref name="primaryId"/> is dropped; confidence is clamped
    /// to [0, 1]; the result is capped at 3 entries. Returns null when the array is absent or empty.
    /// </summary>
    private static IReadOnlyList<ParsedAlternative>? MapAlternatives(JsonElement lineEl, Guid? primaryId)
    {
        if (!lineEl.TryGetProperty("alternatives", out var altsEl) || altsEl.ValueKind != JsonValueKind.Array)
            return null;

        const int cap = 3;
        var result = new List<ParsedAlternative>(cap);

        foreach (var alt in altsEl.EnumerateArray())
        {
            if (result.Count >= cap)
                break;

            var altId = GetGuid(alt, "product_id");
            if (altId is null)
                continue; // catalog id required — drop free-text / null entries

            if (primaryId.HasValue && altId.Value == primaryId.Value)
                continue; // extras-only: exclude the primary suggestion

            var altName = GetString(alt, "product_name") ?? string.Empty;
            var rawConfidence = GetDecimal(alt, "confidence") ?? 0m;
            var confidence = Math.Clamp(rawConfidence, 0m, 1m);

            result.Add(new ParsedAlternative(altId, altName, confidence));
        }

        return result.Count > 0 ? result : null;
    }

    // Unwraps a ```json … ``` (or bare ``` … ```) markdown fence, with or without the body on its own
    // line. Group 1 is the payload between the fences; an unfenced response falls through to the
    // trimmed input. Singleline makes '.' span newlines so multi-line JSON is captured whole.
    private static readonly Regex FencePattern = new(
        @"^\s*```(?:json)?\s*(.*?)\s*```\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string StripFences(string raw)
    {
        var match = FencePattern.Match(raw);
        return match.Success ? match.Groups[1].Value : raw.Trim();
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static decimal? GetDecimal(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var v) ? v : null;

    // An each-count estimate is only meaningful when strictly positive — the untrusted model may emit 0,
    // a negative, or null; all collapse to null (no estimate) rather than seeding a bogus conversion.
    private static decimal? GetPositiveDecimal(JsonElement el, string name) =>
        GetDecimal(el, name) is { } v && v > 0m ? v : null;

    private static Guid? GetGuid(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var v) ? v : null;

    // Untrusted display data: a date/time the AI could not render as the requested shape is dropped
    // (null) rather than guessed. DateOnly/TimeOnly parse invariantly so locale never shifts the value.
    private static DateOnly? GetDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
        && DateOnly.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;

    private static TimeOnly? GetTime(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
        && TimeOnly.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;

    /// <summary>
    /// Converts the AI's string confidence label to a numeric score for the histogram.
    /// high=1.0, low=0.5, none/unknown=0.0.
    /// </summary>
    internal static double ConfidenceScore(string? label) => label switch
    {
        "high" => 1.0,
        "low"  => 0.5,
        _      => 0.0,
    };

    private static void RecordTokenUsage(Activity? activity, ChatTokenUsage? usage)
    {
        if (usage is null || activity is null) return;
        activity.SetTag("ai.usage.input_tokens", usage.InputTokenCount);
        activity.SetTag("ai.usage.output_tokens", usage.OutputTokenCount);
    }
}
