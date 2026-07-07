using Plantry.Deals.Domain;

namespace Plantry.Deals.Application;

/// <summary>
/// A catalog product offered to the matcher as a possible resolution for a <see cref="RawDeal"/>. The
/// deal-side twin of Intake's <c>ProductHint</c>: the caller (the P5-6 <c>IngestFlyer</c> worker, via
/// <c>ICatalogProductReader</c>) fetches these and passes them <b>in</b> — the matcher never touches
/// Catalog. <see cref="Id"/> is the only id the matcher may return: a suggested id that is not one of
/// the passed-in candidates is an invention and is dropped (ADR-007).
/// </summary>
/// <param name="Id">catalog.product id — the sole legal value for a suggested match.</param>
/// <param name="Name">The product's display name, shown to the model for matching.</param>
/// <param name="Brand">The product's brand, if known — helps disambiguate branded advertised items.</param>
public sealed record ProductCandidate(Guid Id, string Name, string? Brand = null);

/// <summary>
/// The stage-2 matcher (DJ2 step 4 / §8): resolve a <b>batch</b> of normalized <see cref="RawDeal"/>s to
/// candidate <c>catalog.product</c>s, returning per deal the AI's suggested id, a <see cref="MatchConfidence"/>,
/// and short reasoning. The <b>untrusted AI twin</b> of <see cref="IFlyerSource"/> (ADR-007), and the AI half
/// of the two-stage match the worker runs for the items a memory lookup missed.
/// <para>
/// <b>Batched to control cost.</b> A full-volume flyer (451 items) resolved one-completion-per-item, each
/// re-sending the whole candidate catalog, is the AI-gateway's dominant cost driver (plantry-04ji). The
/// caller hands the whole memory-miss set to a single call; the adapter chunks internally (one completion
/// per chunk, the candidate catalog sent once per chunk) so the application loop stays dumb — it never
/// sees the chunk size.
/// </para>
/// <para>
/// <b>Candidates are passed in, not fetched here.</b> Exactly like <c>GeminiReceiptParser</c> takes
/// <c>catalogHints</c>, the caller supplies the candidate set; this keeps the adapter catalog-free and
/// unit-testable with faked candidates. The fetch via <c>ICatalogProductReader</c> is the caller's job.
/// </para>
/// <para>
/// <b>A proposal only — never a write (DD6).</b> The surface returns <see cref="MatchProposal"/>s and
/// <b>cannot</b> set <c>product_id</c>: the write-once landing of a suggestion into a <see cref="Deal"/>'s
/// <c>suggested_*</c> / <c>match_confidence</c> / <c>match_reasoning</c> quarantine columns is the caller's
/// (P5-6) contract, verified here by omission.
/// </para>
/// <para>
/// <b>Soft-fail per item (ADR-007).</b> The AI seam is fragile: a failed chunk (API error/empty response)
/// soft-fails <b>all its items</b> to <see cref="MatchProposal.Unmatched"/>, and within a chunk a single
/// malformed/missing/duplicate/out-of-set item degrades <b>only that item</b> — never the chunk, and the
/// surface <b>never throws into the caller</b>, mirroring <c>GeminiReceiptParser</c>.
/// </para>
/// </summary>
public interface IDealMatcher
{
    /// <summary>
    /// Propose a catalog product for each deal in <paramref name="deals"/> from <paramref name="candidates"/>.
    /// The returned list is <b>positionally aligned</b> with <paramref name="deals"/> (same length, same
    /// order): result <c>[i]</c> is the proposal for <c>deals[i]</c>. Returns suggestions only; a suggested
    /// id outside <paramref name="candidates"/> is dropped (never invented), every per-item failure soft-fails
    /// that position to <see cref="MatchProposal.Unmatched"/>, and the call never throws into the caller. An
    /// empty input yields an empty result (no completions issued).
    /// </summary>
    Task<IReadOnlyList<MatchProposal>> MatchBatchAsync(
        IReadOnlyList<RawDeal> deals, IReadOnlyList<ProductCandidate> candidates, CancellationToken ct = default);
}
