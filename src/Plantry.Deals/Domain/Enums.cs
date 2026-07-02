namespace Plantry.Deals.Domain;

/// <summary>
/// The matcher's confidence in a proposed product match. Reuses Intake's High/Low/None scale verbatim
/// (DM-15 / DL-O7) so the review-form treatment carries over. Shapes review treatment only — it
/// <b>never</b> auto-confirms (only <see cref="DealMatchMemory"/> does, D5). Persisted as lowercase text
/// (<c>high</c>/<c>low</c>/<c>none</c>) with a DB CHECK.
/// </summary>
public enum MatchConfidence
{
    High,
    Low,
    None,
}

/// <summary>
/// A <see cref="Deal"/>'s review lifecycle (DD1). Persisted as lowercase text
/// (<c>pending</c>/<c>confirmed</c>/<c>rejected</c>) with a DB CHECK.
/// </summary>
public enum DealStatus
{
    Pending,
    Confirmed,
    Rejected,
}

/// <summary>
/// How a <see cref="Deal"/> originated. Only <c>flyer</c> is built in v1; <c>manual</c> is left in the
/// model for the deferred manual-entry path (D12). Persisted as lowercase text with a DB CHECK.
/// </summary>
public enum DealSource
{
    Flyer,
    Manual,
}

/// <summary>
/// A <see cref="FlyerImport"/>'s async-ingestion lifecycle. Monotonic: <c>Pulling → Parsed</c> or
/// <c>Pulling → Failed</c> (DD12). Persisted as lowercase text
/// (<c>pulling</c>/<c>parsed</c>/<c>failed</c>) with a DB CHECK.
/// </summary>
public enum PullStatus
{
    Pulling,
    Parsed,
    Failed,
}
