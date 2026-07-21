using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Housekeeping.Domain;

/// <summary>
/// Aggregate root (tidy-up.md T5/T9): a household's decision to suppress one finding. The <b>only</b>
/// thing Tidy Up persists — findings themselves are computed live and never stored (T4). One tombstone
/// exists per <c>(HouseholdId, DetectorId, SubjectId)</c> key at any time; dismissing again after a
/// finding has reopened (its <see cref="FactsFingerprint"/> changed) <see cref="Supersede"/>s the same
/// row in place rather than accumulating a new one, so exactly one tombstone ever backs a given key.
/// </summary>
public sealed class Dismissal : AggregateRoot<DismissalId>
{
    private Dismissal() { } // EF

    private Dismissal(
        DismissalId id, HouseholdId householdId, DetectorId detectorId, Guid subjectId,
        string factsFingerprint, DateTimeOffset dismissedAtUtc)
        : base(id)
    {
        HouseholdId = householdId;
        DetectorId = detectorId;
        SubjectId = subjectId;
        FactsFingerprint = factsFingerprint;
        DismissedAtUtc = dismissedAtUtc;
    }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Half of the finding key (T5) — which detector this tombstone suppresses.</summary>
    public DetectorId DetectorId { get; private set; }

    /// <summary>The other half of the finding key — the subject entity (e.g. a product or recipe-line ordinal).</summary>
    public Guid SubjectId { get; private set; }

    /// <summary>
    /// The stable hash of the facts that made the finding true <i>as rendered when the user clicked
    /// Dismiss</i> (§4). Suppression requires an exact match; any fact change reopens the finding.
    /// </summary>
    public string FactsFingerprint { get; private set; } = string.Empty;

    public DateTimeOffset DismissedAtUtc { get; private set; }

    /// <summary>Factory — dismisses a finding as currently rendered.</summary>
    public static Dismissal Create(
        HouseholdId householdId, DetectorId detectorId, Guid subjectId, string factsFingerprint, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(factsFingerprint))
            throw new ArgumentException("Facts fingerprint must not be blank.", nameof(factsFingerprint));

        return new Dismissal(
            DismissalId.New(), householdId, detectorId, subjectId, factsFingerprint, clock.UtcNow);
    }

    /// <summary>
    /// Re-dismisses the same finding key with its current fingerprint (the finding had reopened after
    /// a fact change, and the user dismissed it again) — updates this tombstone in place instead of a
    /// second row accumulating for the same key.
    /// </summary>
    public void Supersede(string factsFingerprint, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(factsFingerprint))
            throw new ArgumentException("Facts fingerprint must not be blank.", nameof(factsFingerprint));

        FactsFingerprint = factsFingerprint;
        DismissedAtUtc = clock.UtcNow;
    }
}
