using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Plantry.Ai.Infrastructure;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// <see cref="IDealMatcher"/> over an OpenAI-compatible <c>ChatClient</c> — the untrusted stage-2 match
/// (DJ2 step 4). The deal-side twin of <c>GeminiReceiptParser</c>: a normalized <see cref="RawDeal"/> plus
/// the caller-supplied candidate products go in, a single best-guess <see cref="MatchProposal"/> comes back.
/// Uses the same global <see cref="AiOptions"/>/<c>ChatClient</c> setup Intake and MealPlanning use today —
/// no per-household key (DM-7 is unbuilt; if it lands, all three adapters adopt it together).
///
/// The AI is an untrusted external function (ADR-007): the surface returns a proposal and <b>cannot</b>
/// write <c>product_id</c> — the write-once quarantine landing (DD6) is the P5-6 caller's job. Every failure
/// path — an API error, an empty response, malformed JSON, or a suggested id the model invented (not in the
/// candidate set) — is a <em>soft</em> failure: it degrades to <see cref="MatchProposal.Unmatched"/> and
/// never throws into the caller.
///
/// Observability (Gate 9): each call is wrapped in an <see cref="Activity"/> span (<c>deal_match</c>) with
/// <c>ai.model</c>, <c>ai.usage.input_tokens</c>, and <c>ai.usage.output_tokens</c> attributes; the
/// resulting confidence is recorded on <see cref="DealMatchTelemetry.DealMatchConfidence"/>. Failures set the span
/// to <see cref="ActivityStatusCode.Error"/> and emit a <c>LogError</c>. No advertised text, candidate
/// content, prompt, or API key is written to any log or span attribute.
/// </summary>
public sealed class DealMatcher : IDealMatcher
{
    private readonly ChatClient _chat;
    private readonly string _modelId;
    private readonly ILogger<DealMatcher> _logger;

    public DealMatcher(
        IOptions<AiOptions> options,
        ILogger<DealMatcher> logger)
    {
        _logger = logger;
        var ai = options.Value;
        _modelId = ai.Model;
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(ai.BaseUrl) };
        _chat = new OpenAIClient(new ApiKeyCredential(ai.ApiKey), clientOptions)
            .GetChatClient(ai.Model);
    }

    private const string SystemPrompt = """
        You match ONE advertised grocery-flyer deal to a household's product catalog. Pick the single
        best catalog product for the advertised item, or none if nothing in the list is a credible match.
        Return ONLY valid JSON, no markdown.

        Output format:
        {
          "suggested_product_id": "catalog UUID copied verbatim from the candidate list, or null",
          "confidence": "high | low | none",
          "reasoning": "one short sentence explaining the pick, or why nothing matched"
        }

        Rules:
        - suggested_product_id MUST be copied verbatim from the candidate list below, or null. Never
          invent an id, and never return an id that is not in the list.
        - confidence: "high" = clearly the same product; "low" = plausible but uncertain; "none" = nothing
          in the candidate list is a reasonable match (then suggested_product_id is null).
        - Match on product identity (name, brand, size), not on price. Brand and size disambiguate.
        - reasoning: one short sentence. Do not repeat the raw fields verbatim.
        """;

    public async Task<MatchProposal> MatchAsync(
        RawDeal deal, IReadOnlyList<ProductCandidate> candidates, CancellationToken ct = default)
    {
        // Gate 9: span wraps the full AI call (latency-sensitive, most likely failure point).
        // Attributes: model id and token usage only — no advertised text, candidate content, or API key.
        using var activity = AiTelemetry.ActivitySource.StartActivity("deal_match");
        activity?.SetTag("ai.model", _modelId);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "AI deal match starting. Model: {Model}, Candidates: {CandidateCount}.",
            _modelId, candidates.Count);

        try
        {
            var userMessage = new UserChatMessage(BuildUserMessage(deal, candidates));

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
                    "AI deal match returned an empty response. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                    _modelId, sw.ElapsedMilliseconds);
                return MatchProposal.Unmatched();
            }

            var proposal = MapResponse(rawText, candidates);

            DealMatchTelemetry.DealMatchConfidence.Record(ConfidenceScore(proposal.Confidence));

            sw.Stop();
            _logger.LogInformation(
                "AI deal match completed. Model: {Model}, Confidence: {Confidence}, Matched: {Matched}, ElapsedMs: {ElapsedMs}.",
                _modelId, proposal.Confidence, proposal.SuggestedProductId is not null, sw.ElapsedMilliseconds);

            return proposal;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "AI deal match failed with exception. Model: {Model}, ElapsedMs: {ElapsedMs}.",
                _modelId, sw.ElapsedMilliseconds);
            return MatchProposal.Unmatched();
        }
    }

    private static string BuildUserMessage(RawDeal deal, IReadOnlyList<ProductCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Advertised deal:");
        sb.Append("  name: ").AppendLine(deal.RawName);
        if (!string.IsNullOrWhiteSpace(deal.Brand))
            sb.Append("  brand: ").AppendLine(deal.Brand);
        if (!string.IsNullOrWhiteSpace(deal.Size))
            sb.Append("  size: ").AppendLine(deal.Size);
        if (!string.IsNullOrWhiteSpace(deal.SaleStory))
            sb.Append("  sale_story: ").AppendLine(deal.SaleStory);
        sb.AppendLine();

        sb.AppendLine("Candidate catalog products (id — name — brand):");
        foreach (var c in candidates)
        {
            sb.Append('[').Append(c.Id).Append("] ").Append(c.Name);
            if (!string.IsNullOrWhiteSpace(c.Brand))
                sb.Append(" — ").Append(c.Brand);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Maps the model's raw text response into a <see cref="MatchProposal"/>. Strips markdown fences,
    /// parses the JSON, and enforces the untrusted-match contract (ADR-007): a positive suggestion is
    /// trusted only when <b>both</b> hold — the suggested id is copied verbatim from
    /// <paramref name="candidates"/> (an invented or out-of-set id is dropped) <b>and</b> the confidence is
    /// <c>high</c>/<c>low</c>. If either is missing (invalid id, or a <c>none</c>/unknown confidence) the
    /// proposal carries no product and collapses to <see cref="MatchConfidence.None"/> with a null id — the
    /// same shape as <see cref="MatchProposal.Unmatched"/>; the free-text reasoning is preserved regardless.
    /// Any malformed/empty content soft-fails to <see cref="MatchProposal.Unmatched"/>. Extracted as a pure
    /// static method so it is unit-testable against recorded fixtures with no live call.
    /// </summary>
    internal static MatchProposal MapResponse(string? rawContent, IReadOnlyList<ProductCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return MatchProposal.Unmatched();

        try
        {
            using var doc = JsonDocument.Parse(StripFences(rawContent));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return MatchProposal.Unmatched();

            var confidence = ParseConfidence(GetString(root, "confidence"));
            var reasoning = GetString(root, "reasoning");

            // ADR-007: the id is trusted only if it is one of the passed-in candidates. An invented or
            // out-of-set id is dropped rather than committed.
            var suggestedId = GetGuid(root, "suggested_product_id");
            var validId = suggestedId is { } id && candidates.Any(c => c.Id == id) ? suggestedId : null;

            // A positive match needs both a valid product and a high/low confidence. If the id was
            // dropped, or the model itself said "none" (or an unrecognised label), the proposal carries no
            // product — collapse to the Unmatched shape so a None confidence never rides alongside an id.
            if (validId is null || confidence == MatchConfidence.None)
                return new MatchProposal(null, MatchConfidence.None, reasoning);

            return new MatchProposal(validId, confidence, reasoning);
        }
        catch (JsonException)
        {
            return MatchProposal.Unmatched();
        }
    }

    // Unwraps a ```json … ``` (or bare ``` … ```) markdown fence; an unfenced response falls through to
    // the trimmed input. Singleline makes '.' span newlines so multi-line JSON is captured whole.
    private static readonly Regex FencePattern = new(
        @"^\s*```(?:json)?\s*(.*?)\s*```\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string StripFences(string raw)
    {
        var match = FencePattern.Match(raw);
        return match.Success ? match.Groups[1].Value : raw.Trim();
    }

    /// <summary>Maps the AI's string confidence label to the domain enum; unknown/absent ⇒ None.</summary>
    internal static MatchConfidence ParseConfidence(string? label) => label switch
    {
        "high" => MatchConfidence.High,
        "low" => MatchConfidence.Low,
        _ => MatchConfidence.None,
    };

    /// <summary>Numeric score for the confidence histogram: high=1.0, low=0.5, none=0.0.</summary>
    internal static double ConfidenceScore(MatchConfidence confidence) => confidence switch
    {
        MatchConfidence.High => 1.0,
        MatchConfidence.Low => 0.5,
        _ => 0.0,
    };

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static Guid? GetGuid(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
        && Guid.TryParse(p.GetString(), out var v) ? v : null;

    private static void RecordTokenUsage(Activity? activity, ChatTokenUsage? usage)
    {
        if (usage is null || activity is null) return;
        activity.SetTag("ai.usage.input_tokens", usage.InputTokenCount);
        activity.SetTag("ai.usage.output_tokens", usage.OutputTokenCount);
    }
}
