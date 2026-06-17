namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Domain service for computing the effective constraint set for a meal assignment.
/// Given the meal's effective attendee set and their dietary preferences, returns
/// the union of hard stances (Required/Restricted) and the average of soft stances
/// (Preferred/Disliked). Used for advisory warnings only — never blocks save (C9).
/// </summary>
public sealed class MealConstraintResolver
{
    /// <summary>
    /// Resolves constraints for the given meal, returning the effective attendee set
    /// and any hard-stance violations as a human-readable warning string.
    /// </summary>
    /// <param name="meal">The planned meal.</param>
    /// <param name="slot">The meal slot (supplies the default attendee list).</param>
    /// <param name="preferences">All UserPreference records for attendees in this household.</param>
    /// <param name="dishTagIds">Tag IDs on all dishes in the meal (used for hard-stance check).</param>
    /// <returns>The resolved constraint result.</returns>
    public MealConstraints Resolve(
        PlannedMeal meal,
        MealSlot slot,
        IReadOnlyList<UserPreference> preferences,
        IReadOnlyList<Guid> dishTagIds)
    {
        // Effective attendees: override ?? slot default (M4)
        var effectiveAttendees = meal.AttendeesOverride ?? slot.DefaultAttendees;

        // Only consider preferences for effective attendees
        var attendeePrefs = preferences
            .Where(p => effectiveAttendees.Contains(p.UserId))
            .ToList();

        // Union of hard stances (Required/Restricted) across all attendees
        var hardStances = attendeePrefs
            .SelectMany(p => p.TagStances)
            .Where(ts => ts.Stance is "Required" or "Restricted")
            .ToLookup(ts => ts.TagId, ts => ts.Stance);

        // Find violations: a dish has a Restricted-tagged ingredient, or lacks a Required tag
        var violations = new List<string>();
        foreach (var tagId in dishTagIds)
        {
            if (hardStances.Contains(tagId))
            {
                foreach (var stance in hardStances[tagId])
                {
                    // This tag appears in the dish; if any attendee has it Restricted, that's a violation
                    if (stance == "Restricted")
                        violations.Add($"Dish contains tag {tagId} which is Restricted for an attendee.");
                }
            }
        }

        // Check Required: if any attendee has a Required tag that is absent from all dishes
        foreach (var pref in attendeePrefs)
        {
            foreach (var stance in pref.TagStances.Where(ts => ts.Stance == "Required"))
            {
                if (!dishTagIds.Contains(stance.TagId))
                    violations.Add($"Meal is missing required tag {stance.TagId} for attendee {pref.UserId}.");
            }
        }

        string? warning = violations.Count > 0
            ? $"Dietary conflict: {string.Join("; ", violations.Distinct().Take(3))}. You can still save this meal — Plantry won't block you."
            : null;

        return new MealConstraints(
            EffectiveAttendees: [..effectiveAttendees],
            HardStanceWarning: warning);
    }
}

/// <summary>The resolved constraint set for a meal assignment.</summary>
public sealed record MealConstraints(
    IReadOnlyList<Guid> EffectiveAttendees,
    /// <summary>Non-null when a hard stance would be violated. Advisory only — does not block save (C9).</summary>
    string? HardStanceWarning);
