namespace Plantry.Housekeeping.Application;

/// <summary>
/// How urgently a detector's findings matter (tidy-up.md §3). Ordinal order is the render order
/// (T2: "groups ordered by severity, behaviour-affecting first") — do not reorder these members.
/// </summary>
public enum Severity
{
    /// <summary>B — something is wrong <i>now</i> elsewhere in the app (e.g. a false "out", a blocked Cook).</summary>
    BehaviorAffecting = 0,

    /// <summary>A — a capability is silently unavailable, but nothing is actively misbehaving yet.</summary>
    Advisory = 1,
}
