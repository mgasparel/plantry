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
/// (DJ2 step 4). The deal-side twin of <c>GeminiReceiptParser</c>: a batch of normalized <see cref="RawDeal"/>s
/// plus the caller-supplied candidate products go in, one best-guess <see cref="MatchProposal"/> per deal
/// comes back. Uses the same global <see cref="AiOptions"/>/<c>ChatClient</c> setup Intake and MealPlanning
/// use today — no per-household key (DM-7 is unbuilt; if it lands, all three adapters adopt it together).
///
/// <b>Batched (plantry-04ji).</b> The candidate catalog is the same for every item in a sweep, so re-sending
/// it once per item is the AI-gateway's dominant cost. The adapter chunks the input (<see cref="DealMatcherOptions.ChunkSize"/>)
/// and issues <b>one completion per chunk</b> — the catalog sent once per chunk, the items sent as a numbered
/// list — cutting a 451-item flyer from 451 completions to ~12. Chunking lives here so the application loop
/// stays dumb; the caller hands over the whole memory-miss set and gets a positionally-aligned result back.
///
/// The AI is an untrusted external function (ADR-007): the surface returns proposals and <b>cannot</b>
/// write <c>product_id</c> — the write-once quarantine landing (DD6) is the P5-6 caller's job. Every failure
/// path is a <em>soft</em> failure: a whole chunk that faults (API error, empty response) unmatches all its
/// items; within a chunk a malformed element, a missing index, a duplicate index, or a suggested id the model
/// invented degrades <b>only that item</b> to <see cref="MatchProposal.Unmatched"/>. Nothing throws into the caller.
///
/// Observability (Gate 9): each completion is wrapped in an <see cref="Activity"/> span (<c>deal_match</c>) with
/// <c>ai.model</c>, <c>ai.batch.item_count</c>, <c>ai.usage.input_tokens</c>, and <c>ai.usage.output_tokens</c>
/// attributes; each resulting confidence is recorded on <see cref="DealMatchTelemetry.DealMatchConfidence"/>
/// per item. Failures set the span to <see cref="ActivityStatusCode.Error"/> and emit a <c>LogError</c>. No
/// advertised text, candidate content, prompt, or API key is written to any log or span attribute.
/// </summary>
public sealed class DealMatcher : IDealMatcher
{
    private readonly ChatClient _chat;
    private readonly string _modelId;
    private readonly int _chunkSize;
    private readonly ILogger<DealMatcher> _logger;

    public DealMatcher(
        IOptions<AiOptions> options,
        IOptions<DealMatcherOptions> matcherOptions,
        ILogger<DealMatcher> logger)
        : this(CreateClient(options.Value), options, matcherOptions, logger)
    {
    }

    // Test seam (plantry-uurp): lets unit tests script the completion boundary so the chunk-partition loop
    // and one-completion-per-chunk behaviour can be asserted directly (they sit behind the concrete ChatClient
    // and are invisible to the pure MapBatchResponse mapper). Production always routes through the public ctor
    // above, which builds the real client and delegates here — no behaviour, public-API, or DI change.
    internal DealMatcher(
        ChatClient chat,
        IOptions<AiOptions> options,
        IOptions<DealMatcherOptions> matcherOptions,
        ILogger<DealMatcher> logger)
    {
        _logger = logger;
        _chat = chat;
        _modelId = options.Value.Model;
        // A misconfigured 0/negative chunk size degrades to one item per completion rather than dividing by zero.
        _chunkSize = Math.Max(1, matcherOptions.Value.ChunkSize);
    }

    private static ChatClient CreateClient(AiOptions ai) =>
        new OpenAIClient(new ApiKeyCredential(ai.ApiKey), new OpenAIClientOptions { Endpoint = new Uri(ai.BaseUrl) })
            .GetChatClient(ai.Model);

    private const string SystemPrompt = """
        You match advertised grocery-flyer deals to a household's product catalog. You are given ONE
        candidate catalog and a NUMBERED list of advertised items. For EACH advertised item, pick the
        single best catalog product, or none if nothing in the list is a credible match.
        Return ONLY valid JSON, no markdown.

        Output format — a JSON array with exactly one object per advertised item:
        [
          {
            "index": 0,
            "suggested_product_id": "catalog UUID copied verbatim from the candidate list, or null",
            "confidence": "high | low | none",
            "reasoning": "one short sentence explaining the pick, or why nothing matched"
          }
        ]

        Rules:
        - Return exactly one object per advertised item. "index" is the item's number from the list below.
        - suggested_product_id MUST be copied verbatim from the candidate list below, or null. Never
          invent an id, and never return an id that is not in the list.
        - confidence: "high" = clearly the same product; "low" = plausible but uncertain; "none" = nothing
          in the candidate list is a reasonable match (then suggested_product_id is null).
        - Match on product identity (name, brand, size), not on price. Brand and size disambiguate.
        - reasoning: one short sentence. Do not repeat the raw fields verbatim.
        """;

    public async Task<IReadOnlyList<MatchProposal>> MatchBatchAsync(
        IReadOnlyList<RawDeal> deals, IReadOnlyList<ProductCandidate> candidates, CancellationToken ct = default)
    {
        if (deals.Count == 0)
            return [];

        var results = new MatchProposal[deals.Count];

        // One completion per chunk (the cost lever): the candidate catalog is sent once per chunk, the items
        // as a numbered list. A chunk that faults soft-fails only its own slice; sibling chunks proceed.
        for (var start = 0; start < deals.Count; start += _chunkSize)
        {
            var length = Math.Min(_chunkSize, deals.Count - start);
            var chunk = new RawDeal[length];
            for (var i = 0; i < length; i++)
                chunk[i] = deals[start + i];

            var chunkProposals = await MatchChunkAsync(chunk, candidates, ct);
            for (var i = 0; i < length; i++)
                results[start + i] = chunkProposals[i];
        }

        return results;
    }

    /// <summary>
    /// Resolves one chunk in a single completion, returning a proposal per <paramref name="chunk"/> item
    /// (positionally aligned). Every failure is soft: an API error or empty response unmatches the whole
    /// chunk; a per-item defect is contained by <see cref="MapBatchResponse"/>.
    /// </summary>
    private async Task<IReadOnlyList<MatchProposal>> MatchChunkAsync(
        IReadOnlyList<RawDeal> chunk, IReadOnlyList<ProductCandidate> candidates, CancellationToken ct)
    {
        // Gate 9: span wraps the full AI call (latency-sensitive, most likely failure point).
        // Attributes: model id, batch item count, and token usage only — no advertised text, candidate
        // content, or API key.
        using var activity = AiTelemetry.ActivitySource.StartActivity("deal_match");
        activity?.SetTag("ai.model", _modelId);
        activity?.SetTag("ai.batch.item_count", chunk.Count);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "AI deal match starting. Model: {Model}, Items: {ItemCount}, Candidates: {CandidateCount}.",
            _modelId, chunk.Count, candidates.Count);

        try
        {
            var userMessage = new UserChatMessage(BuildUserMessage(chunk, candidates));

            var response = await _chat.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), userMessage],
                cancellationToken: ct);

            var completion = response.Value;
            RecordTokenUsage(activity, completion.Usage);

            var rawText = completion.Content.Count > 0 ? completion.Content[0].Text : null;

            // Gate 9: empty response is a soft failure but must surface as an error span + log. The whole
            // chunk unmatches — sibling chunks are unaffected.
            if (string.IsNullOrWhiteSpace(rawText))
            {
                activity?.SetStatus(ActivityStatusCode.Error, "AI returned an empty response.");
                _logger.LogError(
                    "AI deal match returned an empty response. Model: {Model}, Items: {ItemCount}, ElapsedMs: {ElapsedMs}.",
                    _modelId, chunk.Count, sw.ElapsedMilliseconds);
                return Unmatched(chunk.Count);
            }

            var proposals = MapBatchResponse(rawText, candidates, chunk.Count);

            // Confidence histogram records per item (unchanged semantics — one sample per deal).
            foreach (var proposal in proposals)
                DealMatchTelemetry.DealMatchConfidence.Record(ConfidenceScore(proposal.Confidence));

            sw.Stop();
            var matched = proposals.Count(p => p.SuggestedProductId is not null);
            _logger.LogInformation(
                "AI deal match completed. Model: {Model}, Items: {ItemCount}, Matched: {Matched}, ElapsedMs: {ElapsedMs}.",
                _modelId, chunk.Count, matched, sw.ElapsedMilliseconds);

            return proposals;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "AI deal match failed with exception. Model: {Model}, Items: {ItemCount}, ElapsedMs: {ElapsedMs}.",
                _modelId, chunk.Count, sw.ElapsedMilliseconds);
            return Unmatched(chunk.Count);
        }
    }

    private static string BuildUserMessage(IReadOnlyList<RawDeal> deals, IReadOnlyList<ProductCandidate> candidates)
    {
        var sb = new StringBuilder();

        // Catalog once, up front — shared by every item in the chunk (the whole point of batching).
        sb.AppendLine("Candidate catalog products (id — name — brand):");
        foreach (var c in candidates)
        {
            sb.Append('[').Append(c.Id).Append("] ").Append(c.Name);
            if (!string.IsNullOrWhiteSpace(c.Brand))
                sb.Append(" — ").Append(c.Brand);
            sb.Append('\n');
        }
        sb.AppendLine();

        sb.AppendLine("Advertised items to match (return one result object per index):");
        for (var i = 0; i < deals.Count; i++)
        {
            var deal = deals[i];
            sb.Append("Item ").Append(i).Append(':').Append('\n');
            sb.Append("  name: ").AppendLine(deal.RawName);
            if (!string.IsNullOrWhiteSpace(deal.Brand))
                sb.Append("  brand: ").AppendLine(deal.Brand);
            if (!string.IsNullOrWhiteSpace(deal.Size))
                sb.Append("  size: ").AppendLine(deal.Size);
            if (!string.IsNullOrWhiteSpace(deal.SaleStory))
                sb.Append("  sale_story: ").AppendLine(deal.SaleStory);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Maps the model's raw text response into a positionally-aligned list of <see cref="MatchProposal"/>s,
    /// one per input item (<paramref name="expectedCount"/>). Strips markdown fences, parses the JSON array,
    /// and applies the untrusted-match contract (ADR-007) <b>per item</b>: a positive suggestion is trusted
    /// only when <b>both</b> hold — the suggested id is copied verbatim from <paramref name="candidates"/>
    /// (an invented or out-of-set id is dropped) <b>and</b> the confidence is <c>high</c>/<c>low</c>; otherwise
    /// the item collapses to <see cref="MatchConfidence.None"/> with a null id (reasoning preserved).
    /// <para>
    /// Per-item soft-fail without poisoning the chunk: a malformed array element, an element with a missing/
    /// out-of-range index, or a <b>duplicate</b> index all leave the affected position at
    /// <see cref="MatchProposal.Unmatched"/> — every unaddressed position defaults to Unmatched too. Only a
    /// non-array / unparseable payload unmatches the entire chunk. Extracted as a pure static method so it is
    /// unit-testable against recorded fixtures with no live call.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<MatchProposal> MapBatchResponse(
        string? rawContent, IReadOnlyList<ProductCandidate> candidates, int expectedCount)
    {
        var results = Unmatched(expectedCount);
        if (expectedCount == 0 || string.IsNullOrWhiteSpace(rawContent))
            return results;

        try
        {
            using var doc = JsonDocument.Parse(StripFences(rawContent));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return results; // whole chunk unmatches — a non-array shape is unusable

            var seen = new HashSet<int>();
            var duplicated = new HashSet<int>();

            foreach (var element in root.EnumerateArray())
            {
                // A malformed element (not an object, or no usable index) is skipped; its intended item just
                // stays Unmatched. It never corrupts a sibling item's slot.
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var index = GetInt(element, "index");
                if (index is not { } idx || idx < 0 || idx >= expectedCount)
                    continue;

                if (!seen.Add(idx))
                {
                    // Two elements claim the same index — trust neither. Force that item back to Unmatched.
                    duplicated.Add(idx);
                    continue;
                }

                results[idx] = MapElement(element, candidates);
            }

            foreach (var idx in duplicated)
                results[idx] = MatchProposal.Unmatched();

            return results;
        }
        catch (JsonException)
        {
            return Unmatched(expectedCount); // unparseable payload — whole chunk soft-fails
        }
    }

    /// <summary>
    /// Applies the ADR-007 per-item contract to a single result object: a valid (in-candidate-set) id plus a
    /// high/low confidence is trusted; anything else collapses to the Unmatched shape with reasoning kept.
    /// </summary>
    private static MatchProposal MapElement(JsonElement element, IReadOnlyList<ProductCandidate> candidates)
    {
        var confidence = ParseConfidence(GetString(element, "confidence"));
        var reasoning = GetString(element, "reasoning");

        // ADR-007: the id is trusted only if it is one of the passed-in candidates. An invented or
        // out-of-set id is dropped rather than committed.
        var suggestedId = GetGuid(element, "suggested_product_id");
        var validId = suggestedId is { } id && candidates.Any(c => c.Id == id) ? suggestedId : null;

        // A positive match needs both a valid product and a high/low confidence. If the id was dropped, or
        // the model itself said "none" (or an unrecognised label), collapse to the Unmatched shape so a None
        // confidence never rides alongside an id.
        if (validId is null || confidence == MatchConfidence.None)
            return new MatchProposal(null, MatchConfidence.None, reasoning);

        return new MatchProposal(validId, confidence, reasoning);
    }

    private static MatchProposal[] Unmatched(int count)
    {
        var arr = new MatchProposal[count];
        Array.Fill(arr, MatchProposal.Unmatched());
        return arr;
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

    private static int? GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
        && p.TryGetInt32(out var v) ? v : null;

    private static void RecordTokenUsage(Activity? activity, ChatTokenUsage? usage)
    {
        if (usage is null || activity is null) return;
        activity.SetTag("ai.usage.input_tokens", usage.InputTokenCount);
        activity.SetTag("ai.usage.output_tokens", usage.OutputTokenCount);
    }
}
