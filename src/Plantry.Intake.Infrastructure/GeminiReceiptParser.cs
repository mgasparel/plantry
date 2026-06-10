using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Plantry.Intake.Application;

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
/// </summary>
public sealed class GeminiReceiptParser : IReceiptParser
{
    private readonly ChatClient _chat;

    public GeminiReceiptParser(IOptions<AiOptions> options)
    {
        var ai = options.Value;
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
          "lines": [
            {
              "line_no": 1,
              "receipt_text": "the item text as printed",
              "suggested_product_id": "catalog UUID from the list, or null",
              "suggested_product_name": "the matched catalog name, or null",
              "quantity": 1,
              "unit": "kg or lb for weight-priced items, otherwise null",
              "price": 5.49,
              "confidence": "high | low | none"
            }
          ]
        }

        Rules:
        - One line per purchased product. Skip savings lines, subtotals, loyalty points, totals, headers.
        - price = final amount paid for that line after discounts.
        - quantity = weight (kg/lb) for weight-priced items, otherwise the item count.
        - suggested_product_id MUST be copied verbatim from the catalog list below, or null.
        - confidence: "high" = clear match; "low" = plausible but uncertain; "none" = nothing reasonable
          in the catalog (then suggested_product_id and suggested_product_name are null).
        """;

    public async Task<ReceiptParseResult> ParseAsync(
        byte[] imageBytes,
        string contentType,
        IReadOnlyList<ProductHint> catalogHints,
        CancellationToken ct = default)
    {
        try
        {
            var userMessage = new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(BuildCatalogBlock(catalogHints)),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), contentType));

            var response = await _chat.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), userMessage],
                cancellationToken: ct);

            return MapResponse(response.Value.Content[0].Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptParseResult(null, [], $"Receipt parsing failed: {ex.Message}");
        }
    }

    private static string BuildCatalogBlock(IReadOnlyList<ProductHint> hints)
    {
        var sb = new StringBuilder("Match against this catalog (id — name — skus):\n");
        foreach (var h in hints)
        {
            sb.Append('[').Append(h.Id).Append("] ").Append(h.Name);
            if (h.SkuLabels.Count > 0)
                sb.Append(" — ").Append(string.Join(", ", h.SkuLabels));
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
                    lines.Add(new ParsedLine(
                        LineNo: GetInt(el, "line_no") ?? index,
                        ReceiptText: GetString(el, "receipt_text") ?? string.Empty,
                        SuggestedProductName: GetString(el, "suggested_product_name"),
                        SuggestedProductId: GetGuid(el, "suggested_product_id"),
                        Quantity: GetDecimal(el, "quantity"),
                        UnitLabel: GetString(el, "unit"),
                        Price: GetDecimal(el, "price"),
                        Confidence: GetString(el, "confidence"),
                        RawJson: el.GetRawText()));
                }
            }

            return new ReceiptParseResult(merchant, lines);
        }
        catch (JsonException ex)
        {
            return new ReceiptParseResult(null, [], $"AI returned unparseable JSON: {ex.Message}");
        }
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

    private static Guid? GetGuid(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var v) ? v : null;
}
