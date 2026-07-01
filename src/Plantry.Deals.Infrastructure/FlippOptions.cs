namespace Plantry.Deals.Infrastructure;

/// <summary>
/// Configuration for the Flipp flyer source (<see cref="FlyerSource"/>), bound from the <c>Deals:Flipp</c>
/// configuration section. Flipp is an <b>unofficial, unauthenticated</b> feed (D1 — the single fragile
/// external seam); all of its endpoint shape lives here and in <see cref="FlyerSource"/>. There is no API
/// key — the feed is public — so nothing here is a secret.
/// </summary>
public sealed class FlippOptions
{
    public const string SectionName = "Deals:Flipp";

    /// <summary>
    /// Base URL of the Flipp flyers API. A trailing slash is enforced when the typed <c>HttpClient</c> is
    /// configured so the relative <c>data</c> / <c>flyers/{id}/flyer_items</c> paths resolve correctly.
    /// </summary>
    public string BaseUrl { get; set; } = "https://flyers-ng.flippback.com/api/flipp/";

    /// <summary>Locale sent to Flipp; scopes the feed to a market. v1 targets Canada.</summary>
    public string Locale { get; set; } = "en-CA";

    /// <summary>
    /// Browser-shaped User-Agent sent with each request. Flipp's unofficial feed rejects non-browser
    /// agents; this carries no identity and is not a secret.
    /// </summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
}
