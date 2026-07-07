using System.ClientModel.Primitives;

namespace Plantry.Tests.Unit.TestSupport;

/// <summary>
/// A deliberately dumb <see cref="PipelineResponse"/> — the minimum an OpenAI-SDK
/// <c>ClientResult&lt;T&gt;</c> needs to exist. <see cref="ScriptedChatClient"/> wraps a canned
/// <c>ChatCompletion</c> in a <see cref="System.ClientModel.ClientResult"/>, and
/// <c>ClientResult.FromValue</c> requires a concrete <see cref="PipelineResponse"/> (the type is
/// abstract). No adapter under test reads the transport response — it reads only the deserialized
/// value — so every member here returns an inert success (status 200, empty content, no headers).
/// Hand-rolled: no Moq/NSubstitute, no new NuGet package. Shared across the AI-adapter test-seam
/// tickets (plantry-uurp and the follow-ups it discovered).
/// </summary>
internal sealed class FakePipelineResponse : PipelineResponse
{
    private static readonly BinaryData Empty = BinaryData.FromBytes(Array.Empty<byte>());
    private Stream? _contentStream = new MemoryStream(Array.Empty<byte>());

    public override int Status => 200;

    public override string ReasonPhrase => "OK";

    public override Stream? ContentStream
    {
        get => _contentStream;
        set => _contentStream = value;
    }

    public override BinaryData Content => Empty;

    protected override PipelineResponseHeaders HeadersCore { get; } = new EmptyHeaders();

    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Empty;

    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
        new(Empty);

    public override void Dispose() => _contentStream?.Dispose();

    /// <summary>An always-empty header collection — nothing under test inspects response headers.</summary>
    private sealed class EmptyHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
    }
}
