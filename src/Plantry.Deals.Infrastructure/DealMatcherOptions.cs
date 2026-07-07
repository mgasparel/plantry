namespace Plantry.Deals.Infrastructure;

/// <summary>
/// Configuration for <see cref="DealMatcher"/>, bound from <c>Deals:Matcher</c>.
/// <para>
/// <b>Chunking is a cost lever (plantry-04ji).</b> The matcher sends the household's whole candidate
/// catalog once per completion, so batching N flyer items into a single completion cuts input-token spend
/// from items&#215;catalog toward chunks&#215;catalog. <see cref="ChunkSize"/> is the number of memory-miss items
/// packed into one completion: bigger chunks mean fewer completions but a longer per-call prompt/response
/// (and a coarser soft-fail blast radius — a failed chunk unmatches all its items). The default of 40
/// resolves a 451-item flyer in ~12 completions instead of 451.
/// </para>
/// </summary>
public sealed class DealMatcherOptions
{
    public const string SectionName = "Deals:Matcher";

    /// <summary>
    /// Flyer items packed into one AI completion. Clamped to at least 1 by the adapter, so a misconfigured
    /// 0/negative value degrades to one-item-per-completion rather than dividing by zero. Default 40.
    /// </summary>
    public int ChunkSize { get; set; } = 40;
}
