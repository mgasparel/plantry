namespace Plantry.Deals.Domain;

/// <summary>
/// The stage-2 match proposal for a <see cref="Deal"/> — the matcher's (memory or AI) suggested product,
/// its <see cref="MatchConfidence"/>, and free-text reasoning. This is the <b>ACL quarantine</b> half of
/// the boundary: it is stamped once when the deal is staged and <b>never overwritten</b> after parse
/// (DD6). It is provenance — only the user-resolved <c>ProductId</c>/<c>Status</c> ever commit.
/// </summary>
/// <param name="SuggestedProductId">Soft-ref → catalog.product; the matcher's pick (or null if unmatched).</param>
/// <param name="Confidence">The matcher's confidence (High/Low/None). Never auto-confirms (D5).</param>
/// <param name="Reasoning">The AI's rationale, or "remembered match" for the memory path.</param>
public sealed record MatchProposal(
    Guid? SuggestedProductId,
    MatchConfidence Confidence,
    string? Reasoning)
{
    /// <summary>An empty proposal — nothing suggested, <see cref="MatchConfidence.None"/>.</summary>
    public static MatchProposal Unmatched() => new(null, MatchConfidence.None, null);
}
