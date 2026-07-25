namespace Plantry.Web.Deals;

/// <summary>
/// Configuration for the <see cref="FlyerIngestionWorker"/> (P5-6), bound from <c>Deals:Ingestion</c>.
/// <para>
/// Cadence is <b>once daily</b> by default (locked decision): flyers refresh ~weekly, so a daily sweep
/// catches new ones within a day and re-pulls are idempotent no-ops (content-hash dedup, DD5). The worker's
/// boot due-check (plantry-rb36) runs the first sweep immediately only when one is actually due (no prior
/// pull recorded across any household, or a full <see cref="PollInterval"/> has already elapsed);
/// otherwise it waits out the remainder of the interval before the first tick, so a short-lived test/E2E
/// host boot with a fresh database and no pull history is the one case that still sweeps at boot — see
/// <see cref="FlyerIngestionWorker"/> for the full due-check.
/// </para>
/// <para>
/// <b>Single-instance assumption (known limit):</b> the worker runs in-process in Plantry.Web with no
/// leader election. If Web ever scales out, move ingestion to a dedicated single-instance worker or add
/// leader election to avoid double-pulls — not v1.
/// </para>
/// </summary>
public sealed class FlyerIngestionOptions
{
    public const string SectionName = "Deals:Ingestion";

    /// <summary>Master switch. Default on; a host that must never pull (some test rigs) can set it false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the worker sweeps every household. Default 24h (flyers refresh ~weekly).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(24);
}
