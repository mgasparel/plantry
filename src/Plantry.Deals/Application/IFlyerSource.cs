using System.Globalization;
using Plantry.Deals.Domain;

namespace Plantry.Deals.Application;

/// <summary>
/// A merchant returned by the flyer-source directory search (Flipp). <see cref="ExternalRef"/> is the
/// stable directory id an <c>EnsureStore</c> subscribes on (back-filled onto <c>catalog.store</c>);
/// <see cref="Name"/> is the display label the user picks from.
/// </summary>
public sealed record DirectoryMerchant(string ExternalRef, string Name);

/// <summary>
/// The <see cref="IFlyerSource"/> pull output for one store's current flyer (N4): the raw advertised
/// items plus the dedup/validity envelope (<c>flyer_external_id</c> + window + raw content). Consumed by
/// the P5-6 <c>IngestFlyer</c> worker; <b>unused in the P5-2 directory-search slice</b> — it is defined
/// here so the port is complete and P5-3's real adapter does not have to redefine it.
/// <para>
/// <b>Soft-fail (ADR-007, the Intake untrusted-source pattern).</b> The Flipp seam is fragile, so a
/// pull that fails — a network/HTTP error, an empty or malformed payload, or no active flyer — returns
/// an <em>error-carrying result</em> (<see cref="ErrorMessage"/> set, <see cref="HasError"/> true) rather
/// than throwing into the caller. The P5-6 worker maps <see cref="HasError"/> to
/// <c>FlyerImport.MarkFailed</c>. On a failure result <see cref="Window"/> is <c>null</c> and
/// <see cref="Deals"/> is empty; on success <see cref="Window"/> is non-null and <see cref="ErrorMessage"/>
/// is <c>null</c>. Build failures with <see cref="Failed"/>.
/// </para>
/// </summary>
/// <param name="FilteredItemCount">How many raw flyer rows the adapter dropped as non-product marketing
/// decoration (e.g. Flipp's $0 "PRICE DROP" / "ALWAYS LOW PRICE" chrome) before they could become
/// <see cref="Deals"/>. Diagnostic only — surfaced on the pull log line so the AI-matcher-cost saving is
/// observable; it never changes what the domain persists. Zero on a soft-failure result.</param>
public sealed record FlyerPullResult(
    string FlyerExternalId,
    ValidityWindow? Window,
    string RawContent,
    IReadOnlyList<RawDeal> Deals,
    string? ErrorMessage = null,
    int FilteredItemCount = 0)
{
    /// <summary>True when the pull soft-failed; the caller maps this to <c>FlyerImport.MarkFailed</c>.</summary>
    public bool HasError => ErrorMessage is not null;

    // ASCII unit separator: a control char that cannot appear in Flipp's advertised text fields, so it
    // delimits the projected fields without any risk of a value colliding with the delimiter.
    private const char DedupSeparator = '\u001F';

    /// <summary>
    /// The canonical <b>dedup hash input</b> (DD5) — an order-normalized projection of <em>only</em> the
    /// fields that become a <see cref="Deal"/> (name / brand / size / price / quantity / unit / sale_story)
    /// plus the flyer window and <c>flyer_external_id</c>. The P5-6 worker hashes <b>this</b>, not the
    /// verbatim <see cref="RawContent"/>.
    /// <para>
    /// <b>Why (plantry-04ji.4).</b> Flipp embeds volatile per-item chrome in the raw <c>flyer_items</c>
    /// payload (impression/view/click counters, generated timestamps) and can reorder items between pulls,
    /// so a SHA-256 over <see cref="RawContent"/> churns day-over-day even when the advertised deals are
    /// unchanged — re-staging every still-Pending deal and re-running the AI matcher over an unchanged
    /// flyer daily. Projecting to the meaning-bearing fields and sorting the item lines makes an unchanged
    /// flyer hash <em>identically</em> across pulls, so the DD5 byte-identical no-op fires. The volatile
    /// fields never enter <see cref="RawDeal"/> (they are dropped at the ACL boundary), so they can never
    /// reach this projection. <see cref="RawContent"/> is still stored verbatim as <c>raw_flyer</c>
    /// provenance (DD6) — untouched; only the dedup input is canonicalized.
    /// </para>
    /// </summary>
    public string DedupContent
    {
        get
        {
            var window = Window is { } w
                ? $"{w.ValidFrom:yyyy-MM-dd}{DedupSeparator}{w.ValidTo:yyyy-MM-dd}"
                : string.Empty;

            var header = string.Join(DedupSeparator, FlyerExternalId, window);

            var items = Deals
                .Select(d => string.Join(
                    DedupSeparator,
                    d.RawName,
                    d.Brand ?? string.Empty,
                    d.Size ?? string.Empty,
                    d.Price.ToString(CultureInfo.InvariantCulture),
                    d.Quantity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    d.UnitId?.ToString() ?? string.Empty,
                    d.SaleStory ?? string.Empty))
                .OrderBy(line => line, StringComparer.Ordinal); // order-normalized: Flipp may reshuffle items

            return string.Join('\n', items.Prepend(header));
        }
    }

    /// <summary>
    /// Builds a soft-failure result: no window, no deals, carrying <paramref name="error"/>. The
    /// <paramref name="rawContent"/> (the payload we did manage to read, if any) is preserved for
    /// provenance/debugging; <see cref="FlyerExternalId"/> is empty since a failed pull has no flyer id.
    /// </summary>
    public static FlyerPullResult Failed(string error, string rawContent = "") =>
        new(string.Empty, null, rawContent, [], error);
}

/// <summary>
/// The single untrusted, fragile external seam over Flipp (D1). Two halves:
/// <see cref="SearchDirectoryAsync"/> — the postal-code-scoped directory search P5-2 needs — and
/// <see cref="PullFlyerAsync"/> — the flyer pull the P5-6 worker needs.
/// <para>
/// <b>P5-2 ships a stub</b> (<c>StubFlyerSourceAdapter</c>) that returns canned directory results and
/// leaves the pull unimplemented; <b>P5-3 swaps in the real Flipp adapter against this same port</b>
/// (it must not redefine it — flag that the port already exists). Output is untrusted and quarantined
/// by callers (ADR-007); the domain only ever sees <see cref="RawDeal"/>s.
/// </para>
/// </summary>
public interface IFlyerSource
{
    /// <summary>
    /// Directory search: the merchants with active flyers near <paramref name="postalCode"/>, optionally
    /// filtered by <paramref name="nameQuery"/>. Flipp's feed is postal-code-scoped, so the location is
    /// required — a blank postal code returns no results (deals.md §store_subscription DECISION).
    /// </summary>
    Task<IReadOnlyList<DirectoryMerchant>> SearchDirectoryAsync(
        string postalCode, string? nameQuery, CancellationToken ct = default);

    /// <summary>
    /// Pull a subscribed store's current flyer for <paramref name="postalCode"/>. Implemented by the real
    /// P5-3 adapter and driven by the P5-6 worker; the P5-2 stub does not implement it.
    /// </summary>
    Task<FlyerPullResult> PullFlyerAsync(
        string externalRef, string postalCode, CancellationToken ct = default);
}
