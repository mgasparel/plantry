using System.ClientModel;
using System.Text;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace Plantry.Tests.Unit.TestSupport;

/// <summary>
/// A hand-rolled <see cref="ChatClient"/> test double (no Moq/NSubstitute, no new NuGet package) that lets a
/// unit test script the completion boundary of any OpenAI-compatible AI adapter. The OpenAI SDK 2.x is built
/// for exactly this: <see cref="ChatClient"/> has a protected parameterless constructor and a virtual
/// <see cref="CompleteChatAsync(IEnumerable{ChatMessage}, ChatCompletionOptions, CancellationToken)"/>, and
/// <see cref="OpenAIChatModelFactory"/> mints canned <see cref="ChatCompletion"/> values.
///
/// <para>
/// The seam records every invocation (see <see cref="Calls"/>) so a test can assert <b>how many</b> completions
/// an adapter issued and inspect each outgoing prompt, and it scripts the <b>response per call index</b> via a
/// responder delegate — which may return a canned completion, throw an arbitrary exception (fault injection), or
/// return whitespace-only content (soft-fail path). This is the reusable half of the plantry-ftgp epic: the four
/// other AI adapters adopt this same double verbatim.
/// </para>
///
/// <para>
/// Adapters call the collection overload — e.g. DealMatcher's
/// <c>_chat.CompleteChatAsync([system, user], cancellationToken: ct)</c> — which binds to the virtual
/// <c>CompleteChatAsync(IEnumerable&lt;ChatMessage&gt;, ChatCompletionOptions, CancellationToken)</c> overridden
/// below (the <c>params</c> convenience overload cannot bind a named <c>cancellationToken:</c>). If an adapter
/// ever resolved to a different member, its recorded completion count would be zero and the seam's own count
/// assertions would fail loudly — the double is self-guarding.
/// </para>
/// </summary>
internal sealed class ScriptedChatClient : ChatClient
{
    private static readonly Regex ItemLine = new(@"^Item \d+:", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly Func<int, IReadOnlyList<ChatMessage>, ChatCompletion> _responder;

    /// <summary>Every recorded invocation, in call order.</summary>
    public List<ScriptedCall> Calls { get; } = [];

    /// <summary>The number of times the completion boundary was crossed — the headline cost metric.</summary>
    public int CallCount => Calls.Count;

    /// <param name="responder">
    /// Builds the response for a given zero-based call index and the messages it received. Throw from here to
    /// simulate an API fault (an <see cref="OperationCanceledException"/> propagates; anything else is a soft
    /// fail in a well-behaved adapter). Return <see cref="Completion"/> with whitespace to exercise the
    /// empty-response path.
    /// </param>
    public ScriptedChatClient(Func<int, IReadOnlyList<ChatMessage>, ChatCompletion> responder)
        : base()
    {
        _responder = responder;
    }

    public override Task<ClientResult<ChatCompletion>> CompleteChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var callIndex = Calls.Count;
        Calls.Add(new ScriptedCall(callIndex, messageList, cancellationToken));

        // The responder controls the outcome. A throw here surfaces synchronously into the adapter's awaited
        // call (evaluated before the await), which is exactly how a real client-side failure would present.
        var completion = _responder(callIndex, messageList);
        var result = ClientResult.FromValue(completion, new FakePipelineResponse());
        return Task.FromResult(result);
    }

    /// <summary>
    /// Builds an Assistant <see cref="ChatCompletion"/> whose single text content part is
    /// <paramref name="jsonContent"/> (what an adapter reads via <c>completion.Content[0].Text</c>). The
    /// <c>outputAudio:</c> named argument disambiguates to the newer factory overload (both overloads share
    /// <c>role</c>/<c>content</c>, so a call without a distinguishing argument is ambiguous).
    /// </summary>
    public static ChatCompletion Completion(string jsonContent) =>
        // OPENAI001: OpenAIChatModelFactory is the SDK's own experimental-but-supported mocking helper for
        // exactly this purpose (building canned completions in tests). Suppressed narrowly at the call site.
#pragma warning disable OPENAI001
        OpenAIChatModelFactory.ChatCompletion(
            role: ChatMessageRole.Assistant,
            content: new ChatMessageContent(jsonContent),
            outputAudio: null);
#pragma warning restore OPENAI001

    /// <summary>
    /// A convenient default responder: it counts the <c>Item N:</c> entries in the user prompt and emits a
    /// valid JSON array with one all-<c>none</c> object per item. Adapters that batch a numbered item list
    /// (DealMatcher) get a correctly-shaped, positionally-complete "nothing matched" response for any chunk
    /// size, so completion-count tests need not hand-write per-call JSON.
    /// </summary>
    public static ChatCompletion UnmatchedFor(IReadOnlyList<ChatMessage> messages)
    {
        var count = ItemCount(messages);
        var sb = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"index\": ").Append(i)
              .Append(", \"suggested_product_id\": null, \"confidence\": \"none\", \"reasoning\": \"no match\"}");
        }
        sb.Append(']');
        return Completion(sb.ToString());
    }

    /// <summary>The number of <c>Item N:</c> lines in the concatenated user prompt of <paramref name="messages"/>.</summary>
    public static int ItemCount(IReadOnlyList<ChatMessage> messages) =>
        ItemLine.Matches(ScriptedCall.UserTextOf(messages)).Count;
}

/// <summary>One recorded <see cref="ScriptedChatClient"/> invocation: its call index, the messages it received, and the token it was handed.</summary>
/// <param name="Index">Zero-based call order.</param>
/// <param name="Messages">The messages the adapter sent (typically <c>[system, user]</c>).</param>
/// <param name="CancellationToken">The token the adapter forwarded.</param>
internal sealed record ScriptedCall(
    int Index,
    IReadOnlyList<ChatMessage> Messages,
    CancellationToken CancellationToken)
{
    /// <summary>The concatenated text of the user message — the prompt the adapter built for this call.</summary>
    public string UserText => UserTextOf(Messages);

    /// <summary>The number of <c>Item N:</c> lines in this call's user prompt.</summary>
    public int ItemCount => ScriptedChatClient.ItemCount(Messages);

    internal static string UserTextOf(IReadOnlyList<ChatMessage> messages)
    {
        var user = messages.OfType<UserChatMessage>().FirstOrDefault();
        if (user is null) return string.Empty;
        return string.Concat(user.Content.Select(part => part.Text));
    }
}
