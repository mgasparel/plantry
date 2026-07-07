using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Plantry.Ai.Infrastructure;
using Plantry.Intake.Application;
using Plantry.Intake.Infrastructure;
using Plantry.Tests.Unit.TestSupport;

namespace Plantry.Tests.Unit.Intake.Infrastructure;

/// <summary>
/// L1 tests for <see cref="GeminiReceiptParser"/>'s completion boundary — the part that sits behind the
/// concrete OpenAI <c>ChatClient</c> and is therefore invisible to <see cref="GeminiReceiptParserTests"/>
/// (which cover only the pure <c>MapResponse</c> mapper). These drive <c>ParseAsync</c> through a scripted
/// <c>ChatClient</c> seam (<see cref="ScriptedChatClient"/>) injected via the adapter's internal test
/// constructor (plantry-rkt1), so the failure contract the mapper never sees can be asserted directly
/// (mirrors <c>RecipeTagSuggesterCompletionTests</c> / <c>DealMatcherChunkingTests</c>).
///
/// <para>
/// Covered: the happy path issues exactly one completion and returns the mapped result; the outgoing prompt
/// carries the household catalog-hint block and the caller's cancellation token (prompt construction +
/// forwarding); an API fault (any non-cancellation exception) soft-fails to an error result — the caller maps
/// it to <c>MarkParsingFailed</c> and nothing throws into the page; a whitespace-only completion soft-fails
/// the same way; an <see cref="OperationCanceledException"/> propagates (the one exception the adapter must
/// NOT swallow); and the Gate-9 telemetry span is set to <see cref="ActivityStatusCode.Error"/> on a soft
/// fail and carries the model tag on success.
/// </para>
/// </summary>
public sealed class GeminiReceiptParserCompletionTests
{
    private static readonly Guid BananaId = Guid.Parse("0193b4a0-1111-7000-8000-000000000001");

    private static readonly IReadOnlyList<ProductHint> Hints =
    [
        new(BananaId, "Bananas", ["4011"], TracksEach: true),
    ];

    // The scripted seam never actually transmits the image, so any bytes / content type suffice.
    private static readonly byte[] Image = [1, 2, 3, 4];

    private const string ValidResponse = """
        {
          "merchant": "Whole Foods Market",
          "lines": [
            {
              "line_no": 1,
              "receipt_text": "ORG BANANAS",
              "suggested_product_id": "0193b4a0-1111-7000-8000-000000000001",
              "suggested_product_name": "Bananas",
              "quantity": 1.2,
              "unit": "kg",
              "price": 1.74,
              "confidence": "high"
            }
          ]
        }
        """;

    private static GeminiReceiptParser Parser(ChatClient chat) =>
        new(chat, Options.Create(new AiOptions { Model = "test-model" }), NullLogger<GeminiReceiptParser>.Instance);

    private static Task<ReceiptParseResult> Parse(GeminiReceiptParser parser, CancellationToken ct = default) =>
        parser.ParseAsync(Image, "image/png", Hints, ct);

    [Fact]
    public async Task Happy_Path_Issues_Exactly_One_Completion_And_Returns_The_Mapped_Result()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        var result = await Parse(Parser(chat));

        Assert.Equal(1, chat.CallCount);
        Assert.False(result.HasError);
        Assert.Equal("Whole Foods Market", result.MerchantText);
        var line = Assert.Single(result.Lines);
        Assert.Equal(BananaId, line.SuggestedProductId);
    }

    [Fact]
    public async Task The_Outgoing_Prompt_Carries_The_System_Prompt_And_Catalog_Hint_Block()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        await Parse(Parser(chat));

        var call = Assert.Single(chat.Calls);
        Assert.IsType<SystemChatMessage>(call.Messages[0]);          // system prompt sent first
        Assert.Contains(BananaId.ToString(), call.UserText);          // catalog id, verbatim
        Assert.Contains("Bananas", call.UserText);                    // catalog name
        Assert.Contains("tracked by: each", call.UserText);           // each-tracking flag for the estimate rule
    }

    [Fact]
    public async Task The_Caller_CancellationToken_Is_Forwarded_To_The_Completion()
    {
        using var cts = new CancellationTokenSource();
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));

        await Parse(Parser(chat), cts.Token);

        var call = Assert.Single(chat.Calls);
        Assert.Equal(cts.Token, call.CancellationToken);
    }

    [Fact]
    public async Task An_Api_Fault_Soft_Fails_To_An_Error_Result_Without_Throwing()
    {
        var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("gateway 500"));

        var result = await Parse(Parser(chat));

        Assert.True(result.HasError);
        Assert.Contains("gateway 500", result.ErrorMessage);
        Assert.Empty(result.Lines);
        Assert.Null(result.MerchantText);
        Assert.Equal(1, chat.CallCount); // the completion was attempted before it faulted
    }

    [Fact]
    public async Task An_Empty_Response_Soft_Fails_To_An_Error_Result()
    {
        var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion("   ")); // whitespace-only

        var result = await Parse(Parser(chat));

        Assert.True(result.HasError);
        Assert.Equal("AI returned an empty response.", result.ErrorMessage);
        Assert.Empty(result.Lines);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task An_OperationCanceledException_Propagates_Out_Of_ParseAsync()
    {
        // The adapter's catch filter excludes OCE — cancellation must surface, not soft-fail to an error result.
        var chat = new ScriptedChatClient((_, _) => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Parse(Parser(chat)));
    }

    [Fact]
    public async Task A_Soft_Failed_Parse_Sets_The_Telemetry_Span_Status_To_Error()
    {
        var spans = CaptureReceiptParseSpans(out var listener);
        using (listener)
        {
            var chat = new ScriptedChatClient((_, _) => throw new InvalidOperationException("gateway 500"));
            await Parse(Parser(chat));
        }

        var span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task A_Successful_Parse_Tags_The_Span_With_The_Model_And_Leaves_The_Status_Unset()
    {
        var spans = CaptureReceiptParseSpans(out var listener);
        using (listener)
        {
            var chat = new ScriptedChatClient((_, _) => ScriptedChatClient.Completion(ValidResponse));
            await Parse(Parser(chat));
        }

        var span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal("test-model", span.GetTagItem("ai.model"));
    }

    /// <summary>
    /// Subscribes an <see cref="ActivityListener"/> to the shared "Plantry.AI" source and captures only the
    /// <c>receipt_parse</c> spans this adapter emits. Filtering by operation name isolates these assertions
    /// from <c>deal_match</c>/<c>recipe_tag_suggest</c> spans other adapter tests may emit on the same source
    /// in parallel; only this (serially-run) class emits <c>receipt_parse</c> from a unit test.
    /// </summary>
    private static List<Activity> CaptureReceiptParseSpans(out ActivityListener listener)
    {
        var captured = new List<Activity>();
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AiTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.OperationName == "receipt_parse")
                    captured.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return captured;
    }
}
