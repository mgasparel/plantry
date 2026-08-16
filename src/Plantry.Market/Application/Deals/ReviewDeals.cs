using Plantry.Market.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Market.Application;

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
    bool AutoMatched,
    Guid? UnitId = null,
    DealPurchaseContext? Purchase = null,
    IReadOnlyList<Guid>? duplicateDealIds = null)
{
    /// <summary>
    /// IDs of other pending deals with the same advertised identity as this view's deal. The projection and
    /// <see cref="ReviewDeals.FindAsync"/> populate this list for hidden duplicate flyer crops; ordinary views
    /// expose an empty list. The public property is always non-null, including direct record construction.
    /// </summary>
    public IReadOnlyList<Guid> DuplicateDealIds { get; } = duplicateDealIds ?? [];

    /// <summary>Compatibility alias for callers that describe these as pending duplicate candidates.</summary>
    public IReadOnlyList<Guid> PendingDuplicateIds => DuplicateDealIds;

    /// <summary>True when a live, resolvable suggested product exists — drives the "did you mean" chip and the
    /// Confirm verb. A <see cref="MatchConfidence.None"/>/"Unrecognized" deal has none, so it can only be
    /// Corrected (search) or Rejected.</summary>
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
/// <param name="Flyers">
/// The <b>pending-only</b> flyer chapters (each with ≥1 pending deal). This is the set every routing/handoff
/// decision keys off — <see cref="FlyerRail.ResolveActiveKey"/>, the active-flyer resolve, and the
/// <c>ShowHandoff</c> check — so a finished flyer leaves it and its last deal triggers the handoff.
/// </param>
/// <param name="DoneFlyers">
/// The Confirm-finished chapters (plantry-8f7v): in-window (store, window) groups with 0 pending and ≥1
/// Confirmed, projected as display-only done chips (PendingCount 0). Kept <b>separate</b> from
/// <see cref="Flyers"/> so it never affects routing/handoff or the progress counts; the rail merges the two
/// only for rendering (<see cref="FlyerRail.Build"/>'s done-last ordering places them after pending). An
/// all-rejected flyer never appears here (Rejected is not browsable — known gap, plantry-wmt7).
/// </param>
public sealed record ReviewQueueProjection(
    IReadOnlyList<FlyerBlock> Flyers,
    IReadOnlyList<FlyerBlock> DoneFlyers,
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
    IClock clock,
    PricingQueries pricingQueries,
    IPurchaseFrequencyReader purchaseFrequency,
    IUnitPriceCalculator unitPriceCalculator)
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
    /// Confirmed count, <c>TotalCount</c> = in-window Pending+Confirmed. <c>ReviewedCount</c> is counted
    /// directly from the browsable set rather than derived by subtraction from <c>TotalCount</c>, because
    /// the pending queue views are duplicate-collapsed by <see cref="CollapseDuplicateFlyerCrops"/> —
    /// subtracting the collapsed count would silently count hidden duplicate crops as "reviewed". The bar
    /// tracks confirmed progress against still-known work; it never double-counts and never regresses on a
    /// re-drive.
    /// </para>
    /// </summary>
    public async Task<ReviewQueueProjection> ProjectPendingQueueAsync(CancellationToken ct = default)
    {
        var all = await deals.ListBrowsableAsync(ct);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var views = await BuildPendingViewsAsync(all, ct);
        var flyers = await ResolveFlyerLinksAsync(GroupIntoFlyers(views, today), ct);
        var doneFlyers = await ResolveFlyerLinksAsync(await BuildDoneFlyersAsync(all, today, ct), ct);

        var inWindow = all.Count(d => today <= d.ValidityWindow.ValidTo);
        var reviewed = all.Count(d => today <= d.ValidityWindow.ValidTo && d.Status == DealStatus.Confirmed); // in-window Confirmed — counted directly, NOT inWindow - views.Count, because views is duplicate-collapsed

        return new ReviewQueueProjection(flyers, doneFlyers, views, reviewed, inWindow);
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

        var collapsed = CollapseDuplicateFlyerCrops(pending);

        var (storeNames, suggestionNames) = await ResolveNamesAsync(collapsed.Select(x => x.Representative).ToList(), ct);

        var views = collapsed.Select(x => ToView(x.Representative, storeNames, suggestionNames, x.SiblingIds)).ToList();
        var purchaseContexts = await BuildPurchaseContextsAsync(views, ct);

        return views
            .Select(v => purchaseContexts.TryGetValue(v.DealId, out var context) ? v with { Purchase = context } : v)
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
    /// Purchase-history context for each view with a resolved suggested product (plantry-gtgl), keyed by
    /// <see cref="DealId"/> — batched over the whole card set (one price-history read, one purchase-dates
    /// read, one latest-purchase read; no N+1 per card), so rendering a flyer with many cards costs three
    /// round trips total instead of three per card. A view's suggested product with no purchase/manual price
    /// history at all is simply absent from the result (the ticket's "skip the row silently") — no entry
    /// with null/zero fields standing in for "unknown". The deal's own unit-price normalization
    /// (<see cref="IUnitPriceCalculator"/>) still runs per-deal — price/quantity/unit are deal attributes,
    /// not product attributes — but the calculator memoizes per unit, so this stays cheap.
    /// </summary>
    private async Task<IReadOnlyDictionary<DealId, DealPurchaseContext>> BuildPurchaseContextsAsync(
        IReadOnlyList<DealReviewView> views, CancellationToken ct)
    {
        var productIds = views
            .Where(v => v.SuggestedProductId is not null)
            .Select(v => v.SuggestedProductId!.Value)
            .Distinct()
            .ToList();
        if (productIds.Count == 0)
            return EmptyPurchaseContexts;

        var histories = await pricingQueries.PriceHistoryForProductsAsync(productIds, ct);
        if (histories.Count == 0)
            return EmptyPurchaseContexts;

        var purchaseDates = await purchaseFrequency.PurchaseDatesForProductsAsync(histories.Keys, ct);
        var latestPurchases = await pricingQueries.LatestPurchasePricesAsync(histories.Keys, ct);

        var result = new Dictionary<DealId, DealPurchaseContext>();
        foreach (var view in views)
        {
            if (view.SuggestedProductId is not { } productId)
                continue;
            if (!histories.TryGetValue(productId, out var history))
                continue; // no purchase history at all — skip silently (the ticket's stated behaviour)
            if (!latestPurchases.TryGetValue(productId, out var latest))
                continue;

            // A zero average (a $0.00 free/promo purchase observation normalizes to a 0m unit price and is
            // still a usable PriceHistoryStats.Average input) would divide-by-zero below — skip the whole
            // context rather than null just PercentDelta, matching DealPurchaseContext's "never a context
            // with null/zero fields standing in for unknown".
            if (PriceHistoryStats.Average(history) is not { } averagePrice || averagePrice <= 0m)
                continue;

            decimal? dealUnitPrice = view.UnitId is { } unitId
                ? await unitPriceCalculator.TryNormalizeAsync(view.Price, view.Quantity ?? 1m, unitId, ct)
                : null;
            var percentDelta = dealUnitPrice is { } dup
                ? Math.Round((dup - averagePrice) / averagePrice * 100m, 1)
                : (decimal?)null;

            var interval = purchaseDates.TryGetValue(productId, out var dates)
                ? PurchaseCadence.AverageInterval(dates)
                : null;

            result[view.DealId] = new DealPurchaseContext(
                averagePrice,
                dealUnitPrice,
                percentDelta,
                interval,
                DateOnly.FromDateTime(latest.ObservedAt.UtcDateTime));
        }
        return result;
    }

    private static readonly IReadOnlyDictionary<DealId, DealPurchaseContext> EmptyPurchaseContexts =
        new Dictionary<DealId, DealPurchaseContext>();

    /// <summary>
    /// Collapses Flipp's duplicate flyer-item crops (plantry-g1u9). Flipp's flyer feed is page-image-based:
    /// each <see cref="RawDeal"/> is a detected image "cutout" at a specific page position, not a
    /// unique-product record, so the same advertised deal is sometimes detected/cropped several times —
    /// <c>FlyerSource.MapItems</c> (Infrastructure) mirrors the feed 1:1 with no dedup, and downstream
    /// staging materializes one <see cref="Deal"/> per raw row, so those repeats land in the pending queue as
    /// fully independent rows that are byte-identical on every advertised field.
    /// <para>
    /// Deliberately a <b>read-side, review-projection</b> fix (safer than touching ingestion or the
    /// aggregate): grouping by (store, validity window, normalized name, price, brand, size, sale story,
    /// quantity) — the full advertised identity, matching every field <see cref="DealReviewView"/> renders —
    /// collapses same-crop repeats to one representative <see cref="Deal"/> per group before it is projected
    /// into a card, so the reviewer sees exactly one card per advertised deal.
    /// The representative remains the only deal confirmed or corrected; hidden pending duplicates are rejected
    /// best-effort by web review orchestration after the primary command succeeds, preventing resolved groups
    /// from resurfacing while keeping the domain commands unchanged.
    /// </para>
    /// <para>
    /// Deterministic pick — oldest <see cref="Deal.CreatedAt"/>, ties broken by <see cref="Deal.Id"/> — keeps
    /// the surviving representative stable across renders instead of flapping between duplicates.
    /// </para>
    /// </summary>
    private sealed record CollapsedDeal(Deal Representative, IReadOnlyList<Guid> SiblingIds);

    private static List<CollapsedDeal> CollapseDuplicateFlyerCrops(IReadOnlyList<Deal> pending) =>
        pending
            .GroupBy(d => (
                d.StoreId, d.ValidityWindow.ValidFrom, d.ValidityWindow.ValidTo,
                d.NormalizedName, d.Price, d.Brand, d.Size, d.SaleStory, d.Quantity))
            .Select(g =>
            {
                var ordered = g.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id.Value).ToList();
                return new CollapsedDeal(ordered[0], ordered.Skip(1).Select(d => d.Id.Value).ToList());
            })
            .ToList();

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
    /// Confirm-finished flyer chapters as display-only done chips (plantry-8f7v, option a). From the browsable
    /// set (Pending+Confirmed; Rejected excluded), a (store, validity-window) group that is <b>in-window</b>
    /// (<c>today ≤ ValidTo</c>, DD14 — consistent with the pending queue, so a past-window done flyer drops
    /// off), has <b>zero</b> pending, and <b>≥1 Confirmed</b> becomes a <see cref="FlyerBlock"/> with no deals
    /// (<see cref="FlyerBlock.PendingCount"/> 0). Store names resolve via the same batch read as the pending
    /// path (no N+1); <see cref="FlyerBlock.FlyerExternalId"/> is stamped by <see cref="ResolveFlyerLinksAsync"/>
    /// afterwards, so a done chip keeps its "View flyer" link. Kept out of the pending
    /// <see cref="ReviewQueueProjection.Flyers"/> so it never influences routing/handoff or progress counts.
    /// <para>
    /// An all-rejected flyer (every deal Rejected) yields no group here — Rejected is not browsable, so its
    /// (store, window) is invisible. That is the known gap tracked in plantry-wmt7 (which adds the rejected-deals
    /// read port); until then such a flyer simply disappears rather than showing a done chip.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<FlyerBlock>> BuildDoneFlyersAsync(
        IReadOnlyList<Deal> all, DateOnly today, CancellationToken ct)
    {
        var doneGroups = all
            .Where(d => today <= d.ValidityWindow.ValidTo)
            .GroupBy(d => (d.StoreId, d.ValidityWindow.ValidFrom, d.ValidityWindow.ValidTo))
            .Where(g => g.All(d => d.Status != DealStatus.Pending)
                        && g.Any(d => d.Status == DealStatus.Confirmed))
            .ToList();
        if (doneGroups.Count == 0)
            return [];

        var storeNames = await stores.ResolveNamesAsync(
            doneGroups.Select(g => g.Key.StoreId).Distinct().ToList(), ct);

        return doneGroups
            .Select(g => new FlyerBlock(
                g.Key.StoreId,
                storeNames.TryGetValue(g.Key.StoreId, out var name) ? name : "(unknown store)",
                g.Key.ValidFrom,
                g.Key.ValidTo,
                Math.Max(0, g.Key.ValidTo.DayNumber - today.DayNumber),
                Deals: []))
            .OrderBy(f => f.ExpiresInDays)
            .ThenBy(f => f.StoreName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
    /// Finds one deal for review and resolves its hidden duplicate-crop cleanup candidates. The target may be
    /// Pending, a hidden Pending sibling, or Confirmed; the returned <see cref="DealReviewView.DuplicateDealIds"/>
    /// contains only other Pending deals with the exact advertised identity tuple (store, validity start/end,
    /// normalized name, price, brand, size, sale story, and quantity). A hidden target therefore discovers its
    /// representative and remaining pending siblings just like the visible representative does.
    /// Returns null when the id is unknown to this household (RLS) or rejected. Rejected targets never produce
    /// sibling candidates. <paramref name="includePurchaseContext"/> defaults to true for the correction card;
    /// single action handlers pass false when they only need the suggestion and sibling IDs.
    /// </summary>
    public async Task<DealReviewView?> FindAsync(
        DealId id, bool includePurchaseContext = true, CancellationToken ct = default)
    {
        var deal = await deals.FindAsync(id, ct);
        if (deal is null || deal.Status == DealStatus.Rejected)
            return null;

        var all = await deals.ListBrowsableAsync(ct);
        var siblings = all
            .Where(d => d.Status == DealStatus.Pending && d.Id != deal.Id && SameAdvertisedIdentity(d, deal))
            .Select(d => d.Id.Value)
            .ToList();

        var (storeNames, suggestionNames) = await ResolveNamesAsync([deal], ct);
        var view = ToView(deal, storeNames, suggestionNames, siblings);
        if (!includePurchaseContext)
            return view;

        var purchaseContexts = await BuildPurchaseContextsAsync([view], ct);
        return purchaseContexts.TryGetValue(view.DealId, out var context) ? view with { Purchase = context } : view;
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
        IReadOnlyDictionary<Guid, DealProductInfo> suggestionNames,
        IReadOnlyList<Guid>? duplicateDealIds = null)
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
            deal.AutoMatched,
            deal.UnitId,
            duplicateDealIds: duplicateDealIds ?? []);
    }


    private static bool SameAdvertisedIdentity(Deal left, Deal right) =>
        left.StoreId == right.StoreId
        && left.ValidityWindow.ValidFrom == right.ValidityWindow.ValidFrom
        && left.ValidityWindow.ValidTo == right.ValidityWindow.ValidTo
        && left.NormalizedName == right.NormalizedName
        && left.Price == right.Price
        && left.Brand == right.Brand
        && left.Size == right.Size
        && left.SaleStory == right.SaleStory
        && left.Quantity == right.Quantity;

    private static readonly IReadOnlyDictionary<Guid, DealProductInfo> EmptyProducts =
        new Dictionary<Guid, DealProductInfo>();
}
