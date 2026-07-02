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
public sealed record FlyerPullResult(
    string FlyerExternalId,
    ValidityWindow? Window,
    string RawContent,
    IReadOnlyList<RawDeal> Deals,
    string? ErrorMessage = null)
{
    /// <summary>True when the pull soft-failed; the caller maps this to <c>FlyerImport.MarkFailed</c>.</summary>
    public bool HasError => ErrorMessage is not null;

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
