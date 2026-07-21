using Plantry.Housekeeping.Domain;

namespace Plantry.Housekeeping.Application;

/// <summary>One still-open finding on the Tidy Up page — a group card row (T2).</summary>
public sealed record OpenFindingRow(Finding Finding);

/// <summary>
/// One dismissed finding, shown under the "Dismissed" disclosure regardless of which detector produced
/// it (the prototype's flat dismissed list, T2) — carries its own dismissal date for the "Dismissed N
/// weeks ago" line.
/// </summary>
public sealed record DismissedFindingRow(Finding Finding, DateTimeOffset DismissedAtUtc);

/// <summary>One detector's group card: title/consequence copy plus its currently-open rows (T2/T4). Detectors with zero open findings produce no group (T2 "empty groups don't render").</summary>
public sealed record TidyUpGroup(
    DetectorId DetectorId,
    string Title,
    string Consequence,
    string IconName,
    Severity Severity,
    IReadOnlyList<OpenFindingRow> Rows);

/// <summary>
/// The whole Tidy Up page read model (tidy-up.md §4): groups ordered severity-first then by detector
/// id, plus every dismissed finding (flat, most-recent first) and the open count the badge cache was
/// just refreshed with.
/// </summary>
public sealed record TidyUpPageResult(
    IReadOnlyList<TidyUpGroup> Groups,
    IReadOnlyList<DismissedFindingRow> Dismissed,
    int OpenCount)
{
    public static readonly TidyUpPageResult Empty = new([], [], 0);

    /// <summary>True when there are no open findings — the "All tidy" empty state (T2). The dismissed
    /// disclosure, if any, still renders below the empty state.</summary>
    public bool IsAllTidy => Groups.Count == 0;
}
