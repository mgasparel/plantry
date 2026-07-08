using Plantry.Deals.Application;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// One rendered chapter of the flyer rail (q9zr.3) — presentation over a <see cref="FlyerBlock"/>. Carries
/// the display-shaped bits the two rail densities need: the urgency flag (warning tone ≤ 2 days), the valid
/// range and expiry labels, and the active/done state. The store name and validity dates live <b>only</b>
/// here (and the active-flyer context line) — never on a card/row (the dedupe ruling from prototyping).
/// </summary>
/// <param name="FlyerUrl">
/// The external "View flyer" URL, or null when no source flyer resolved for this chapter (q9zr.7). Built by
/// <see cref="FlyerRail.Build"/> from <see cref="FlyerBlock.FlyerExternalId"/>: present only when a Parsed
/// <see cref="Plantry.Deals.Domain.FlyerImport"/> was resolved for this (store, window), and points at the
/// verified Flipp store-search fallback (<see cref="FlyerRail.StoreSearchUrl"/>). The rail renders the link
/// slot — in the big chip's meta or the compact active-flyer line, never per card — only when it is present.
/// </param>
public sealed record FlyerRailChapter(
    string Key,
    string StoreName,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    int ExpiresInDays,
    int PendingCount,
    bool IsActive,
    string? FlyerUrl)
{
    /// <summary>Expiry countdown at/under which the chip's days-left badge takes the warning tone (DD14 urgency).</summary>
    public const int UrgentWithinDays = 2;

    /// <summary>True once every deal in this flyer is resolved — the chip sorts last and shows a ✓.</summary>
    public bool IsDone => PendingCount == 0;

    /// <summary>True when this flyer is closing soon (and still has pending work) — drives the warning tone.</summary>
    public bool IsUrgent => !IsDone && ExpiresInDays <= UrgentWithinDays;

    /// <summary>Human validity range, e.g. "Jul 4 – Jul 10" — shown in the big chip meta / active-flyer line.</summary>
    public string ValidRange => $"{ValidFrom:MMM d} – {ValidTo:MMM d}";

    /// <summary>Big-chip days-left badge text, e.g. "14 · 3d left".</summary>
    public string PendingExpiryLabel => $"{PendingCount} · {ExpiresInDays}d left";

    /// <summary>Compact-pill days-left badge text, e.g. "3d".</summary>
    public string ExpiryShort => $"{ExpiresInDays}d";
}

/// <summary>
/// The rendered flyer rail (q9zr.3, scope 2): the ordered chapters plus the density decision and the
/// compact-rail summary line. Density switches at <see cref="CompactThreshold"/> flyers — big
/// <c>.flyer-chip</c> cards at or below it, compact expiry-sorted <c>.flyer-pill</c>s above it with a
/// <c>.rail-summary</c> roll-up. Built by <see cref="Build"/>; pure and static so the density switch,
/// pill ordering (soonest-expiring first, done last), and summary are covered by L4 tests over synthetic
/// blocks (the single-store live seed can only exercise the ≤3 big-chip path).
/// </summary>
public sealed record FlyerRail(IReadOnlyList<FlyerRailChapter> Chapters)
{
    /// <summary>At or below this flyer count the rail renders big chips; above it, compact pills.</summary>
    public const int CompactThreshold = 3;

    /// <summary>True when the rail switches to the compact pill density (&gt; 3 flyers).</summary>
    public bool IsCompact => Chapters.Count > CompactThreshold;

    /// <summary>Flyers still carrying pending work — the "N flyers waiting" count in the summary line.</summary>
    public int WaitingCount => Chapters.Count(c => !c.IsDone);

    /// <summary>Total pending deals across all still-waiting flyers — the "M deals" count in the summary.</summary>
    public int WaitingDeals => Chapters.Where(c => !c.IsDone).Sum(c => c.PendingCount);

    /// <summary>Days to the soonest-expiring pending flyer — 0 when nothing is waiting.</summary>
    public int SoonestExpiryDays =>
        Chapters.Where(c => !c.IsDone).Select(c => c.ExpiresInDays).DefaultIfEmpty(0).Min();

    /// <summary>Summary-line phrasing of the soonest expiry: "today" / "tomorrow" / "in N days".</summary>
    public string SoonestExpiryLabel => SoonestExpiryDays switch
    {
        <= 0 => "today",
        1 => "tomorrow",
        _ => $"in {SoonestExpiryDays} days",
    };

    /// <summary>
    /// Builds the rail from the flyer blocks and the active flyer key. Chapters are ordered done-last, then
    /// soonest-expiring first (ties by store name) — the queue's own order, matching the prototype's
    /// <c>railOrder()</c>. The block matching <paramref name="activeKey"/> is marked active.
    /// </summary>
    public static FlyerRail Build(IReadOnlyList<FlyerBlock> blocks, string? activeKey) =>
        new(blocks
            .OrderBy(b => b.PendingCount == 0)                            // done flyers last
            .ThenBy(b => b.ExpiresInDays)                                 // soonest-expiring first
            .ThenBy(b => b.StoreName, StringComparer.OrdinalIgnoreCase)
            .Select(b => new FlyerRailChapter(
                b.Key, b.StoreName, b.ValidFrom, b.ValidTo, b.ExpiresInDays,
                b.PendingCount, IsActive: b.Key == activeKey,
                // The link renders only when a source flyer was resolved (FlyerExternalId present, q9zr.7);
                // otherwise the slot stays empty.
                FlyerUrl: b.FlyerExternalId is null ? null : StoreSearchUrl(b.StoreName)))
            .ToList());

    /// <summary>Base of the verified Flipp store-search URL (q9zr.7) — the geo-detected search that resolves.</summary>
    public const string FlippSearchBase = "https://flipp.com/en-ca/search/";

    /// <summary>
    /// The "View flyer" link target for a store (q9zr.7): the verified Flipp store-SEARCH URL
    /// <c>https://flipp.com/en-ca/search/{url-encoded store name}</c>. Direct flyer-slug URLs
    /// (<c>flipp.com/en-ca/flyers/{slug}</c>) return 404 (verified 2026-07-07), so the geo-detected search URL
    /// is emitted as the reliable fallback; a future direct deep link can replace it once the Flipp adapter
    /// establishes a working shape (the flyer's external id is carried on <see cref="FlyerBlock"/> for that).
    /// The store name is escaped as a single path segment (spaces → <c>%20</c>).
    /// </summary>
    public static string StoreSearchUrl(string storeName) =>
        FlippSearchBase + Uri.EscapeDataString(storeName.Trim());

    /// <summary>
    /// Resolves the active flyer (q9zr.3, scope 1): the <paramref name="requested"/> flyer when it still has
    /// pending work, otherwise the default — the soonest-expiring flyer with pending work. Returns null only
    /// when nothing is pending. A finished/stale requested key falls through to the default, which is what
    /// drives the per-flyer handoff.
    /// </summary>
    public static string? ResolveActiveKey(IReadOnlyList<FlyerBlock> blocks, string? requested)
    {
        var pending = blocks
            .Where(b => b.PendingCount > 0)
            .OrderBy(b => b.ExpiresInDays)
            .ThenBy(b => b.StoreName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested is not null && pending.Any(b => b.Key == requested))
            return requested;

        return pending.FirstOrDefault()?.Key;
    }
}
