using Plantry.Housekeeping.Domain;

namespace Plantry.Housekeeping.Application;

/// <summary>
/// One entry in the detector catalogue (tidy-up.md §3/§4). Implementations are cross-context read
/// compositions and live in <c>Plantry.Composition/Housekeeping/</c> — this contract is the whole
/// framework surface; adding a detector is one class + one DI registration (§4), no other edits.
/// <see cref="GetTidyUpPageQuery"/> discovers every registered instance via
/// <c>IEnumerable&lt;IProblemDetector&gt;</c>.
/// </summary>
public interface IProblemDetector
{
    /// <summary>Stable identity — the other half of every finding's dismissal key (T5).</summary>
    DetectorId Id { get; }

    /// <summary>Drives render order: behaviour-affecting groups before advisory ones (T2).</summary>
    Severity Severity { get; }

    /// <summary>The group card's <c>&lt;h3&gt;</c> — names the problem class (T2).</summary>
    string GroupTitle { get; }

    /// <summary>The group card's one-sentence "why this matters" copy, shown once per card (T2).</summary>
    string GroupConsequence { get; }

    /// <summary>
    /// Icon sprite id (e.g. <c>"i-scale"</c>) rendered on the group card head, from the shared icon
    /// sprite in <c>_Layout.cshtml</c>.
    /// </summary>
    string IconName { get; }

    /// <summary>
    /// Scans the current household's data and returns every open instance of this detector's gap.
    /// Household-scoped internally (detectors resolve the household from their own read ports'
    /// tenant context); returns an empty list when there is no signed-in household.
    /// </summary>
    Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default);
}
