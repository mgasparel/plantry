using Plantry.Deals.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Application;

/// <summary>
/// One deal projected for the DJ4 review form (P5-8 / §6b — the Intake-review twin). Carries the raw
/// flyer fields verbatim (ACL quarantine, DD6) plus the resolved single match proposal — the suggested
/// product's <see cref="SuggestedProductName"/> (resolved for display), its <see cref="Confidence"/>, and
/// the matcher <see cref="Reasoning"/>. Unlike Intake's line, a deal carries <b>one</b> suggestion, never
/// a ranked alternatives list (P5-4 returns a single <see cref="MatchProposal"/>) — so the "did you mean"
/// affordance is the one suggested-product chip, not a multi-alternative row.
/// </summary>
public sealed record DealReviewView(
    DealId DealId,
    Guid StoreId,
    string StoreName,
    string RawName,
    string? Brand,
    string? SaleStory,
    decimal Price,
    decimal? Quantity,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    MatchConfidence Confidence,
    string? Reasoning,
    Guid? SuggestedProductId,
    string? SuggestedProductName,
    DealStatus Status,
    bool AutoMatched)
{
    /// <summary>
    /// True when a live, resolvable suggested product exists — drives the "did you mean" chip and the
    /// Confirm verb. A <see cref="MatchConfidence.None"/>/"Unrecognized" deal (DL-O7) has none, so it can
    /// only be Corrected (search) or Rejected.
    /// </summary>
    public bool HasSuggestion => SuggestedProductId is not null && SuggestedProductName is not null;

    /// <summary>True for the already-confirmed correction entry path (the DJ3 → DJ4 edge from the active list).</summary>
    public bool IsAlreadyConfirmed => Status == DealStatus.Confirmed;
}

/// <summary>
/// One flyer chapter of the pending review queue (q9zr.3): every still-pending deal sharing a
/// (<see cref="StoreId"/>, validity window) — the guided flow reviews the queue one flyer at a time.
/// <c>ExpiresInDays</c> is the clock-derived countdown to <see cref="ValidTo"/> (DD14 urgency). <see cref="Key"/>
/// is the stable, URL-safe identity used for <c>?flyer=</c> routing so a refresh is idempotent.
/// </summary>
/// <param name="FlyerExternalId">
/// Flipp's flyer id (the DD5 dedup anchor) for this chapter's source <see cref="FlyerImport"/>, or null when
/// no Parsed import resolves for this (store, window) — resolved by
/// <see cref="ReviewDeals.ProjectPendingQueueAsync"/> via a single batch read (q9zr.7). Its presence is what
/// gates the "View flyer" link; the value itself is carried through for a future direct deep link once the
/// Flipp adapter establishes a working flyer-slug URL shape (direct slug URLs 404 today, verified 2026-07-07).
/// </param>
public sealed record FlyerBlock(
    Guid StoreId,
    string StoreName,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    int ExpiresInDays,
    IReadOnlyList<DealReviewView> Deals,
    string? FlyerExternalId = null)
{
    /// <summary>Pending deals in this flyer (the block is projected only from the pending queue).</summary>
    public int PendingCount => Deals.Count;

    /// <summary>Stable, URL-safe routing key — <c>{store:N}_{from}_{to}</c> — unique per (store, window).</summary>
    public string Key => MakeKey(StoreId, ValidFrom, ValidTo);

    /// <summary>Builds the routing key for a (store, window) pair — shared by the projection and the router.</summary>
    public static string MakeKey(Guid storeId, DateOnly validFrom, DateOnly validTo) =>
        $"{storeId:N}_{validFrom:yyyyMMdd}_{validTo:yyyyMMdd}";
}

/// <summary>
/// The pending review queue projected as flyer chapters plus the overall progress counts (q9zr.3).
/// <see cref="ReviewedCount"/>/<see cref="TotalCount"/> feed the "N of M reviewed" header; see
/// <see cref="ReviewDeals.ProjectPendingQueueAsync"/> for the (Rejected-excluded) progress semantics.
/// </summary>
public sealed record ReviewQueueProjection(
    IReadOnlyList<FlyerBlock> Flyers,
    IReadOnlyList<DealReviewView> PendingDeals,
    int ReviewedCount,
    int TotalCount);

/// <summary>
/// <c>ReviewDeals</c> read service (P5-8 / DJ4). Read-only over the <see cref="Deal"/> aggregate + the
/// clock — <b>nothing is stored</b>. Serves the two review-form entry paths (deals-domain-model §7,
/// SPEC §6b):
/// <list type="number">
///   <item><see cref="ListPendingAsync"/> — the pending review queue (<see cref="DealStatus.Pending"/> ∧
///     <c>today ≤ valid_to</c>, DD14 — expired-unreviewed deals silently drop off), each with its resolved
///     single suggestion for the confidence-shaped treatment.</item>
///   <item><see cref="FindAsync"/> — one deal by id, for correcting/rejecting an already-confirmed
///     auto-matched deal arriving from P5-7's active list (the DJ3 → DJ4 edge). Not window-gated: a
///     correction is a valid backfill even past the window (DD14).</item>
/// </list>
/// Resolves suggested-product + store display names via batch reads (no N+1). A normal RLS-scoped request,
/// so the underlying context only ever sees the signed-in household's rows.
/// </summary>
public sealed class ReviewDeals(
    IDealRepository deals,
    ICatalogProductReader products,
    ICatalogStoreReader stores,
    IFlyerImportRepository flyerImports,
    IClock clock)
{
    /// <summary>The pending review queue (DD14: Pending ∧ not yet expired), oldest-expiring first.</summary>
    public async Task<IReadOnlyList<DealReviewView>> ListPendingAsync(CancellationToken ct = default)
    {
        var all = await deals.ListBrowsableAsync(ct);
        return await BuildPendingViewsAsync(all, ct);
    }

    /// <summary>
    /// The pending queue as flyer chapters (q9zr.3) plus the overall review-progress counts. One
    /// <see cref="ListBrowsableAsync"/> read: the pending deals are grouped by (store, validity window)
    /// into <see cref="FlyerBlock"/>s ordered soonest-expiring first (contiguous, matching the flat queue
    /// order), and the progress denominator is derived from the same browsable set.
    /// <para>
    /// <b>Progress semantics.</b> <see cref="ListBrowsableAsync"/> excludes Rejected deals, so a rejected
    /// deal leaves the reviewable set entirely — there is no stateless way to count it. Progress is
    /// therefore computed over in-window Pending+Confirmed: <c>ReviewedCount</c> = the still-open-window
    /// Confirmed count, <c>TotalCount</c> = in-window Pending+Confirmed. The bar tracks confirmed progress
    /// against still-known work; it never double-counts and never regresses on a re-drive.
    /// </para>
    /// </summary>
    public async Task<ReviewQueueProjection> ProjectPendingQueueAsync(CancellationToken ct = default)
    {
        var all = await deals.ListBrowsableAsync(ct);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var views = await BuildPendingViewsAsync(all, ct);
        var flyers = await ResolveFlyerLinksAsync(GroupIntoFlyers(views, today), ct);

        var inWindow = all.Count(d => today <= d.ValidityWindow.ValidTo);
        var reviewed = inWindow - views.Count; // in-window Confirmed (Pending excluded; Rejected not browsable)

        return new ReviewQueueProjection(flyers, views, reviewed, inWindow);
    }

    private async Task<IReadOnlyList<DealReviewView>> BuildPendingViewsAsync(
        IReadOnlyList<Deal> all, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var pending = all
            .Where(d => d.Status == DealStatus.Pending && today <= d.ValidityWindow.ValidTo)
            .ToList();
        if (pending.Count == 0)
            return [];

        var (storeNames, suggestionNames) = await ResolveNamesAsync(pending, ct);

        return pending
            .Select(d => ToView(d, storeNames, suggestionNames))
            .OrderBy(v => v.ValidTo)
            .ThenBy(v => v.StoreName, StringComparer.OrdinalIgnoreCase)
            // Confidence descending — highest first (the 16 one-click confirms lead each flyer/store block
            // instead of being scattered alphabetically). The enum is declared High → Low → None, so ordinal
            // ascending already yields high-confidence-first; this is what the review UI groups its tiers on.
            .ThenBy(v => v.Confidence)
            .ThenBy(v => v.RawName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Pure flyer grouping (q9zr.3 scope 1): pending deals → flyer blocks keyed by (store, validity
    /// window), soonest-expiring first (ties broken by store name), so a store running two overlapping
    /// flyers is two blocks. <c>ExpiresInDays</c> = <c>ValidTo − today</c> (never negative — the queue is
    /// already DD14-gated to <c>today ≤ ValidTo</c>). Static and clock-free so the &gt;3-flyer density and
    /// pill-ordering paths — which the single-store live seed can't exercise — are covered by L4 tests over
    /// synthetic views (epic known-limitation note, q9zr.14).
    /// </summary>
    public static IReadOnlyList<FlyerBlock> GroupIntoFlyers(IReadOnlyList<DealReviewView> pending, DateOnly today) =>
        pending
            .GroupBy(d => (d.StoreId, d.ValidFrom, d.ValidTo))
            .Select(g => new FlyerBlock(
                g.Key.StoreId,
                g.First().StoreName,
                g.Key.ValidFrom,
                g.Key.ValidTo,
                Math.Max(0, g.Key.ValidTo.DayNumber - today.DayNumber),
                g.ToList()))
            .OrderBy(f => f.ExpiresInDays)
            .ThenBy(f => f.StoreName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Attaches each flyer chapter's source-flyer provenance (q9zr.7): batch-resolves the household's Parsed
    /// <see cref="FlyerImport"/>s for the chapters' distinct stores in a single read (no N+1, mirroring
    /// <see cref="ResolveNamesAsync"/>), keys them by (store, validity-window), and stamps the matching
    /// <see cref="FlyerBlock.FlyerExternalId"/>. A chapter whose (store, window) has no Parsed import is left
    /// with a null external id, so the rail renders no "View flyer" link for it. The window is the join key
    /// because a store can run two overlapping flyers (two blocks, two imports).
    /// </summary>
    private async Task<IReadOnlyList<FlyerBlock>> ResolveFlyerLinksAsync(
        IReadOnlyList<FlyerBlock> flyers, CancellationToken ct)
    {
        if (flyers.Count == 0)
            return flyers;

        var storeIds = flyers.Select(f => f.StoreId).Distinct().ToList();
        var refs = await flyerImports.ListParsedRefsByStoresAsync(storeIds, ct);

        var byWindow = new Dictionary<(Guid StoreId, DateOnly ValidFrom, DateOnly ValidTo), string>();
        foreach (var r in refs)
            // TryAdd: on the rare chance a (store, window) has more than one Parsed import, keep the first
            // deterministically rather than throwing — the link target (store search) is identical regardless.
            byWindow.TryAdd((r.StoreId, r.ValidFrom, r.ValidTo), r.FlyerExternalId);

        return flyers
            .Select(f => byWindow.TryGetValue((f.StoreId, f.ValidFrom, f.ValidTo), out var externalId)
                ? f with { FlyerExternalId = externalId }
                : f)
            .ToList();
    }

    /// <summary>
    /// One deal by id for the review form (the already-confirmed correction entry path). Returns null when
    /// the id is unknown to this household (RLS) or the deal has been rejected (nothing left to review).
    /// </summary>
    public async Task<DealReviewView?> FindAsync(DealId id, CancellationToken ct = default)
    {
        var deal = await deals.FindAsync(id, ct);
        if (deal is null || deal.Status == DealStatus.Rejected)
            return null;

        var (storeNames, suggestionNames) = await ResolveNamesAsync([deal], ct);
        return ToView(deal, storeNames, suggestionNames);
    }

    private async Task<(IReadOnlyDictionary<Guid, string> Stores, IReadOnlyDictionary<Guid, DealProductInfo> Products)>
        ResolveNamesAsync(IReadOnlyList<Deal> source, CancellationToken ct)
    {
        var storeNames = await stores.ResolveNamesAsync(
            source.Select(d => d.StoreId).Distinct().ToList(), ct);

        var suggestionIds = source
            .Where(d => d.SuggestedProductId is not null)
            .Select(d => d.SuggestedProductId!.Value)
            .Distinct()
            .ToList();

        var suggestionNames = suggestionIds.Count == 0
            ? EmptyProducts
            : await products.ForProductsAsync(suggestionIds, ct);

        return (storeNames, suggestionNames);
    }

    private static DealReviewView ToView(
        Deal deal,
        IReadOnlyDictionary<Guid, string> storeNames,
        IReadOnlyDictionary<Guid, DealProductInfo> suggestionNames)
    {
        string? suggestedName = deal.SuggestedProductId is { } sid
                                && suggestionNames.TryGetValue(sid, out var info)
            ? info.Name
            : null;

        return new DealReviewView(
            deal.Id,
            deal.StoreId,
            storeNames.TryGetValue(deal.StoreId, out var storeName) ? storeName : "(unknown store)",
            deal.RawName,
            deal.Brand,
            deal.SaleStory,
            deal.Price,
            deal.Quantity,
            deal.ValidityWindow.ValidFrom,
            deal.ValidityWindow.ValidTo,
            deal.MatchConfidence,
            deal.MatchReasoning,
            deal.SuggestedProductId,
            suggestedName,
            deal.Status,
            deal.AutoMatched);
    }

    private static readonly IReadOnlyDictionary<Guid, DealProductInfo> EmptyProducts =
        new Dictionary<Guid, DealProductInfo>();
}
