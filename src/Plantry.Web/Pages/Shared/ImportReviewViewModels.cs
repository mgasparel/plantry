using Plantry.Intake.Domain;

namespace Plantry.Web.Pages.Shared;

/// <summary>
/// One row of the intake review form — the htmx swap target rendered by `_ImportLineRow`. The
/// visual state is derived from <see cref="Status"/> together with whether a catalog product is
/// linked (<see cref="ProductName"/> non-null), mirroring the LineStatus / SuggestedConfidence
/// domain vocabulary so the feature page (plantry-75u) can map an `ImportLine` straight onto it:
///
///   Pending + High            → matched      (confidence badge + Confirm)
///   Pending + None/Low        → unmatched    (resolve via searchable-select)
///   Confirmed (+ CreatedNew)  → confirmed    (· new product accent when CreatedNew)
///   Dismissed                 → dismissed    (dimmed, Add anyway)
///   Committed                 → committed    (locked, no actions)
///
/// URLs are passed in by the consuming page; on the Dev gallery they are placeholders.
/// </summary>
public sealed record ImportLineRowViewModel(
    string LineId,
    string? ProductName,
    string RawText,
    SuggestedConfidence Confidence,
    LineStatus Status,
    string Quantity,
    string Unit,
    string Price,
    string Expiry,
    bool CreatedNew,
    string ConfirmUrl,
    string DismissUrl,
    string RestoreUrl,
    string SaveUrl)
{
    /// <summary>htmx target id for the row's own out-of-band swaps after an action.</summary>
    public string DomId => $"import-line-{LineId}";

    public bool IsMatched => Status == LineStatus.Pending && Confidence == SuggestedConfidence.High;
    public bool IsUnmatched => Status == LineStatus.Pending && Confidence != SuggestedConfidence.High;
    public bool IsConfirmed => Status == LineStatus.Confirmed;
    public bool IsDismissed => Status == LineStatus.Dismissed;
    public bool IsCommitted => Status == LineStatus.Committed;
}

/// <summary>
/// The sticky progress + commit footer of the review form, rendered by `_CommitBar`. Tracks how
/// many lines are resolved (confirmed) out of the total committable lines; the page supplies the
/// commit/discard endpoints (placeholders on the Dev gallery).
/// </summary>
public sealed record CommitBarViewModel(
    int Confirmed,
    int Total,
    string CommitUrl,
    string DiscardUrl)
{
    public int Remaining => Math.Max(0, Total - Confirmed);
    public bool CanCommit => Total > 0 && Confirmed == Total;
    public int Percent => Total == 0 ? 0 : (int)Math.Round(Confirmed / (double)Total * 100);
}
