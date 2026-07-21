using Plantry.Housekeeping.Domain;

namespace Plantry.Housekeeping.Application;

/// <summary>
/// One detected data-health gap (tidy-up.md §4). Purely a read model — never persisted; recomputed on
/// every Tidy Up page render (T4) and re-derived from live data on every dismiss/restore.
/// </summary>
/// <param name="DetectorId">Which detector produced this finding — half of the dismissal key (T5).</param>
/// <param name="SubjectId">The primary entity the finding is about (a product id, a recipe-line ordinal
/// projected onto a stable subject id) — the other half of the dismissal key.</param>
/// <param name="SubjectName">Display name for the row's primary column (e.g. the product name).</param>
/// <param name="Specifics">One-line detail rendered after the subject name (e.g. "3 lb in stock, display unit is ea").</param>
/// <param name="Consequence">The faint per-row "why should I care" sentence.</param>
/// <param name="FixUrl">Deep link to the owning screen where this gap is fixed (T3) — no in-page fix in v1.</param>
/// <param name="FixLabel">The verb naming the destination (e.g. "Fix in Catalog", "Review in recipe").</param>
/// <param name="FactsFingerprint">
/// Stable hash of the facts that make this finding true, computed by the detector (§4 "fingerprint
/// discipline"). Covers only facts whose change should reopen the finding after a dismissal.
/// </param>
public sealed record Finding(
    DetectorId DetectorId,
    Guid SubjectId,
    string SubjectName,
    string Specifics,
    string Consequence,
    string FixUrl,
    string FixLabel,
    string FactsFingerprint);
