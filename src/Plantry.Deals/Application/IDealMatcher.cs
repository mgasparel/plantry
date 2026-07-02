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
/// The stage-2 matcher (DJ2 step 4 / §8): resolve one normalized <see cref="RawDeal"/> to a candidate
/// <c>catalog.product</c>, returning the AI's suggested id, a <see cref="MatchConfidence"/>, and short
/// reasoning. The <b>untrusted AI twin</b> of <see cref="IFlyerSource"/> (ADR-007), and the AI half of
/// the two-stage match the worker runs after a memory lookup misses.
/// <para>
/// <b>Candidates are passed in, not fetched here.</b> Exactly like <c>GeminiReceiptParser</c> takes
/// <c>catalogHints</c>, the caller supplies the candidate set; this keeps the adapter catalog-free and
/// unit-testable with faked candidates. The fetch via <c>ICatalogProductReader</c> is the caller's job.
/// </para>
/// <para>
/// <b>A proposal only — never a write (DD6).</b> The surface returns a <see cref="MatchProposal"/> and
/// <b>cannot</b> set <c>product_id</c>: the write-once landing of the suggestion into a <see cref="Deal"/>'s
/// <c>suggested_*</c> / <c>match_confidence</c> / <c>match_reasoning</c> quarantine columns is the caller's
/// (P5-6) contract, verified here by omission.
/// </para>
/// <para>
/// <b>Soft-fail (ADR-007).</b> The AI seam is fragile: any API error, an empty response, or a malformed
/// payload degrades to <see cref="MatchProposal.Unmatched"/> (<see cref="MatchConfidence.None"/>, no
/// suggested id) and <b>never throws into the caller</b>, mirroring <c>GeminiReceiptParser</c>.
/// </para>
/// </summary>
public interface IDealMatcher
{
    /// <summary>
    /// Propose a catalog product for <paramref name="deal"/> from <paramref name="candidates"/>. Returns
    /// a suggestion only; a suggested id outside <paramref name="candidates"/> is dropped (never invented),
    /// and every failure path soft-fails to <see cref="MatchProposal.Unmatched"/> rather than throwing.
    /// </summary>
    Task<MatchProposal> MatchAsync(
        RawDeal deal, IReadOnlyList<ProductCandidate> candidates, CancellationToken ct = default);
}
