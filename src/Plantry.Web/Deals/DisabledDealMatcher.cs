using Plantry.Deals.Application;
using Plantry.Deals.Domain;

namespace Plantry.Web.Deals;

/// <summary>
/// No-op <see cref="IDealMatcher"/> registered when no AI API key is configured. The real
/// <c>DealMatcher</c> builds an OpenAI <c>ChatClient</c> at construction (which requires a non-empty
/// key), so a keyless host — dev without secrets, or the E2E stack — would fail DI resolution once the
/// P5-6 worker resolves the port. This stand-in lets the host start and degrades matching to
/// <see cref="MatchProposal.Unmatched"/>, exactly as an AI soft-fail would (mirrors
/// <c>DisabledReceiptParser</c>).
/// </summary>
public sealed class DisabledDealMatcher : IDealMatcher
{
    public Task<MatchProposal> MatchAsync(
        RawDeal deal, IReadOnlyList<ProductCandidate> candidates, CancellationToken ct = default)
        => Task.FromResult(MatchProposal.Unmatched());
}
