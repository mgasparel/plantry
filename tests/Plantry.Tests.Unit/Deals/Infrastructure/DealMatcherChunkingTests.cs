using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Plantry.Ai.Infrastructure;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;
using Plantry.Tests.Unit.TestSupport;

namespace Plantry.Tests.Unit.Deals.Infrastructure;

/// <summary>
/// L1 tests for <see cref="DealMatcher"/>'s chunk-partition loop and one-completion-per-chunk behaviour —
/// the part that sits behind the concrete OpenAI <c>ChatClient</c> and is therefore invisible to
/// <see cref="DealMatcherTests"/> (which cover only the pure <c>MapBatchResponse</c> mapper). These exercise
/// the loop through a scripted <c>ChatClient</c> seam (<see cref="ScriptedChatClient"/>) injected via the
/// adapter's internal test constructor, asserting the completion count directly.
///
/// <para>
/// The headline case is the plantry-04ji DEFER (Gate 10B) this ticket discharges: a full-volume 451-item flyer
/// resolves in <c>ceil(451/40) = 12</c> completions, not 451 — the batching cost lever, proven by an executed
/// test rather than inferred from the mapper. The remaining cases pin the behaviours the seam newly makes
/// testable: the single-chunk floor, empty input, cross-chunk positional alignment, per-chunk soft-fail
/// isolation (API fault and empty response), the ChunkSize clamp, the catalog-per-chunk prompt contract, and
/// <see cref="OperationCanceledException"/> propagation (the one exception the adapter must NOT swallow).
/// </para>
/// </summary>
public sealed class DealMatcherChunkingTests
{
    private static readonly Guid MilkId = Guid.Parse("0193b4a0-2222-7000-8000-000000000001");

    private static readonly IReadOnlyList<ProductCandidate> Candidates =
    [
        new(MilkId, "Whole Milk 2%", "Beatrice"),
    ];

    private static readonly ValidityWindow Window =
        ValidityWindow.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7)).Value;

    // The default responder: shape a positionally-complete "nothing matched" array for whatever the chunk holds.
    private static readonly Func<int, IReadOnlyList<ChatMessage>, ChatCompletion> Unmatched =
        (_, messages) => ScriptedChatClient.UnmatchedFor(messages);

    private static RawDeal Deal(string name) => new(name, null, null, 1.99m, null, null, null, Window);

    private static IReadOnlyList<RawDeal> Deals(int count) =>
        Enumerable.Range(0, count).Select(i => Deal($"Deal {i}")).ToArray();

    private static DealMatcher Matcher(ChatClient chat, int chunkSize) =>
        new(
            chat,
            Options.Create(new AiOptions { Model = "test-model" }),
            Options.Create(new DealMatcherOptions { ChunkSize = chunkSize }),
            NullLogger<DealMatcher>.Instance);

    [Fact]
    public async Task Headline_451_Deals_At_ChunkSize_40_Issues_12_Completions_With_451_Aligned_Proposals()
    {
        var chat = new ScriptedChatClient(Unmatched);
        var matcher = Matcher(chat, chunkSize: 40);

        var proposals = await matcher.MatchBatchAsync(Deals(451), Candidates);

        // The DEFER criterion: 12 completions (ceil(451/40)) — the batching win — not 451.
        Assert.Equal(12, chat.CallCount);
        Assert.Equal(451, proposals.Count);
        // 11 full chunks of 40 items + a final remainder chunk of 11.
        var expectedPerCall = Enumerable.Repeat(40, 11).Append(11).ToArray();
        Assert.Equal(expectedPerCall, chat.Calls.Select(c => c.ItemCount).ToArray());
        // Every position is populated (positionally-aligned result of the requested length).
        Assert.All(proposals, p => Assert.Null(p.SuggestedProductId));
    }

    [Theory]
    [InlineData(40)] // N == ChunkSize
    [InlineData(3)]  // N < ChunkSize
    public async Task A_Batch_At_Or_Below_ChunkSize_Issues_Exactly_One_Completion(int dealCount)
    {
        var chat = new ScriptedChatClient(Unmatched);
        var matcher = Matcher(chat, chunkSize: 40);

        var proposals = await matcher.MatchBatchAsync(Deals(dealCount), Candidates);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal(dealCount, proposals.Count);
    }

    [Fact]
    public async Task Empty_Input_Issues_No_Completions_And_Returns_An_Empty_Result()
    {
        var chat = new ScriptedChatClient(Unmatched);
        var matcher = Matcher(chat, chunkSize: 40);

        var proposals = await matcher.MatchBatchAsync([], Candidates);

        Assert.Empty(proposals);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task Proposals_Align_To_Their_Global_Index_Across_Multiple_Chunks()
    {
        // Each deal carries a distinct "P{g}" token and each candidate's name is that same token, so the
        // responder maps every item to the ONE candidate that identifies it — independent of chunking. If the
        // adapter mis-mapped a chunk-local result to the wrong global slot, the identity check below would fail.
        var candidates = Enumerable.Range(0, 5)
            .Select(g => new ProductCandidate(Guid.NewGuid(), $"P{g}"))
            .ToArray();
        var deals = Enumerable.Range(0, 5).Select(g => Deal($"P{g}")).ToArray();

        var chat = new ScriptedChatClient(TokenMatcher(candidates));
        var matcher = Matcher(chat, chunkSize: 2); // chunks: [0,1] [2,3] [4]

        var proposals = await matcher.MatchBatchAsync(deals, candidates);

        Assert.Equal(3, chat.CallCount);
        for (var g = 0; g < candidates.Length; g++)
        {
            Assert.Equal(candidates[g].Id, proposals[g].SuggestedProductId);
            Assert.Equal(MatchConfidence.High, proposals[g].Confidence);
        }
    }

    [Fact]
    public async Task A_Faulting_Chunk_Soft_Fails_Only_Its_Own_Slice()
    {
        // Middle chunk's completion throws an API-style fault; siblings return clean high matches.
        var chat = new ScriptedChatClient((callIndex, messages) =>
            callIndex == 1
                ? throw new InvalidOperationException("gateway 500")
                : AllHighMatch(messages));
        var matcher = Matcher(chat, chunkSize: 2); // chunks: [0,1] [2,3] [4,5]

        var proposals = await matcher.MatchBatchAsync(Deals(6), Candidates);

        Assert.Equal(3, chat.CallCount);
        // Middle chunk (global 2,3) unmatched — no exception escaped MatchBatchAsync.
        Assert.Null(proposals[2].SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposals[2].Confidence);
        Assert.Null(proposals[3].SuggestedProductId);
        // Both sibling chunks intact.
        Assert.Equal(MilkId, proposals[0].SuggestedProductId);
        Assert.Equal(MilkId, proposals[1].SuggestedProductId);
        Assert.Equal(MilkId, proposals[4].SuggestedProductId);
        Assert.Equal(MilkId, proposals[5].SuggestedProductId);
    }

    [Fact]
    public async Task An_Empty_Response_Chunk_Soft_Fails_Only_Its_Own_Slice()
    {
        var chat = new ScriptedChatClient((callIndex, messages) =>
            callIndex == 1
                ? ScriptedChatClient.Completion("   ") // whitespace-only content
                : AllHighMatch(messages));
        var matcher = Matcher(chat, chunkSize: 2); // chunks: [0,1] [2,3] [4,5]

        var proposals = await matcher.MatchBatchAsync(Deals(6), Candidates);

        Assert.Equal(3, chat.CallCount);
        Assert.Null(proposals[2].SuggestedProductId);
        Assert.Null(proposals[3].SuggestedProductId);
        Assert.Equal(MilkId, proposals[0].SuggestedProductId);
        Assert.Equal(MilkId, proposals[5].SuggestedProductId);
    }

    [Fact]
    public async Task ChunkSize_Zero_Clamps_To_One_Item_Per_Completion()
    {
        var chat = new ScriptedChatClient(Unmatched);
        var matcher = Matcher(chat, chunkSize: 0);

        var proposals = await matcher.MatchBatchAsync(Deals(3), Candidates);

        Assert.Equal(3, chat.CallCount); // clamped to 1 → one completion per item
        Assert.All(chat.Calls, c => Assert.Equal(1, c.ItemCount));
        Assert.Equal(3, proposals.Count);
    }

    [Fact]
    public async Task Every_Chunk_Prompt_Carries_The_Whole_Candidate_Catalog()
    {
        var candidates = new[]
        {
            new ProductCandidate(Guid.NewGuid(), "Milk"),
            new ProductCandidate(Guid.NewGuid(), "Cheese"),
            new ProductCandidate(Guid.NewGuid(), "Bread"),
        };
        var chat = new ScriptedChatClient(Unmatched);
        var matcher = Matcher(chat, chunkSize: 2); // 3 chunks over 5 deals

        await matcher.MatchBatchAsync(Deals(5), candidates);

        Assert.Equal(3, chat.CallCount);
        foreach (var call in chat.Calls)
            foreach (var candidate in candidates)
                Assert.Contains(candidate.Id.ToString(), call.UserText);
    }

    [Fact]
    public async Task An_OperationCanceledException_Propagates_Out_Of_MatchBatchAsync()
    {
        // The adapter's catch filter excludes OCE — cancellation must surface, not soft-fail to Unmatched.
        var chat = new ScriptedChatClient((_, _) => throw new OperationCanceledException());
        var matcher = Matcher(chat, chunkSize: 40);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => matcher.MatchBatchAsync(Deals(2), Candidates));
    }

    // A responder that matches every item in a chunk to MilkId with high confidence (a clean, healthy chunk).
    private static ChatCompletion AllHighMatch(IReadOnlyList<ChatMessage> messages)
    {
        var count = ScriptedChatClient.ItemCount(messages);
        var sb = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"index\": ").Append(i)
              .Append(", \"suggested_product_id\": \"").Append(MilkId)
              .Append("\", \"confidence\": \"high\", \"reasoning\": \"m\"}");
        }
        sb.Append(']');
        return ScriptedChatClient.Completion(sb.ToString());
    }

    // A responder that reads each item's "P{n}" name token and matches it to the candidate of the same name,
    // returning the result at the item's chunk-LOCAL index (as the model would). Used to prove global alignment.
    private static Func<int, IReadOnlyList<ChatMessage>, ChatCompletion> TokenMatcher(
        IReadOnlyList<ProductCandidate> candidates)
    {
        var itemPattern = new Regex(@"Item (\d+):\s+name:\s*(P\d+)", RegexOptions.Multiline);
        return (_, messages) =>
        {
            var text = ScriptedCall.UserTextOf(messages);
            var sb = new StringBuilder("[");
            var first = true;
            foreach (Match m in itemPattern.Matches(text))
            {
                var localIndex = int.Parse(m.Groups[1].Value);
                var token = m.Groups[2].Value;
                var candidate = candidates.First(c => c.Name == token);
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"index\": ").Append(localIndex)
                  .Append(", \"suggested_product_id\": \"").Append(candidate.Id)
                  .Append("\", \"confidence\": \"high\", \"reasoning\": \"x\"}");
            }
            sb.Append(']');
            return ScriptedChatClient.Completion(sb.ToString());
        };
    }
}
