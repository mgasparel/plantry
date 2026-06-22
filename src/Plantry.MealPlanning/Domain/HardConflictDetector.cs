namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Pure, stateless domain service that determines whether a meal slot cell is irreconcilable —
/// i.e., no single candidate recipe can simultaneously satisfy every attendee's hard stances.
/// Candidate-aware: "irreconcilable" means the actual candidate pool has no recipe for everyone,
/// not a theoretical tag clash. Pure: no I/O.
/// </summary>
public static class HardConflictDetector
{
    /// <summary>
    /// Evaluates whether the candidate pool can satisfy all attendees with a single dish.
    /// </summary>
    /// <param name="constraints">
    /// Generation constraints carrying per-attendee hard stances (RequiredTagIds, RestrictedTagIds).
    /// </param>
    /// <param name="candidates">The candidate recipe pool for this slot.</param>
    /// <returns>
    /// <see langword="null"/> when a single recipe can satisfy all attendees (reconcilable).
    /// A <see cref="HardStanceConflict"/> descriptor when no candidate satisfies everyone
    /// (irreconcilable): only emitted when ≥2 attendees carry hard stances.
    /// </returns>
    public static HardStanceConflict? Detect(
        GenerationConstraints constraints,
        IReadOnlyList<CandidateRecipe> candidates)
    {
        // Count attendees that actually carry at least one hard stance.
        var attendeesWithStances = constraints.AttendeeStances
            .Where(a => a.RequiredTagIds.Count > 0 || a.RestrictedTagIds.Count > 0)
            .ToList();

        // A conflict is only possible when ≥2 attendees have competing hard stances.
        if (attendeesWithStances.Count < 2)
            return null;

        // Check whether any single candidate satisfies ALL attendees.
        bool any = candidates.Any(c => constraints.AttendeeStances.All(a => Satisfies(c, a)));
        if (any)
            return null;

        // No single recipe satisfies everyone — irreconcilable.
        var attendeeIds = attendeesWithStances.Select(a => a.UserId).ToList();
        var clashingTagIds = attendeesWithStances
            .SelectMany(a => a.RequiredTagIds)
            .Distinct()
            .ToList();

        return new HardStanceConflict(attendeeIds, clashingTagIds);
    }

    private static bool Satisfies(CandidateRecipe candidate, AttendeeHardStances attendee)
        => attendee.RequiredTagIds.All(candidate.TagIds.Contains)
        && !attendee.RestrictedTagIds.Any(candidate.TagIds.Contains);
}

/// <summary>
/// Descriptor for an irreconcilable hard-stance conflict: no single dish in the candidate pool
/// can jointly satisfy every attendee attending this meal slot.
/// </summary>
/// <param name="AttendeeIds">User IDs of attendees whose hard stances clash.</param>
/// <param name="ClashingTagIds">
/// Union of all required tags from conflicting attendees — the set that no single recipe can co-satisfy.
/// </param>
public sealed record HardStanceConflict(
    IReadOnlyList<Guid> AttendeeIds,
    IReadOnlyList<Guid> ClashingTagIds);
