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
    IClock clock)
{
    /// <summary>The pending review queue (DD14: Pending ∧ not yet expired), oldest-expiring first.</summary>
    public async Task<IReadOnlyList<DealReviewView>> ListPendingAsync(CancellationToken ct = default)
    {
        var all = await deals.ListBrowsableAsync(ct);
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
            .ThenBy(v => v.RawName, StringComparer.OrdinalIgnoreCase)
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
