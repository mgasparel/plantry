using Plantry.Intake.Application;

namespace Plantry.Web.Intake;

/// <summary>
/// No-op <see cref="IReceiptParser"/> registered when no AI API key is configured and neither dev
/// seam (<c>UseSampleParser</c> / <c>UseFakeParser</c>) is active. Lets the app start and serve a
/// friendly locked-feature UI instead of crashing at DI resolution time.
/// </summary>
public sealed class DisabledReceiptParser : IReceiptParser
{
    public Task<ReceiptParseResult> ParseAsync(
        byte[] imageBytes,
        string contentType,
        IReadOnlyList<ProductHint> catalogHints,
        CancellationToken ct = default)
        => Task.FromResult(new ReceiptParseResult(null, [], "Receipt scanning is not available: no AI API key is configured."));
}
