namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Per-attendee hard stances collected from <see cref="MealConstraintResolver.ResolveForGeneration"/>.
/// An attendee with no hard stances carries empty collections — they still count toward headcount.
/// </summary>
public sealed record AttendeeHardStances(
    Guid UserId,
    IReadOnlyCollection<Guid> RequiredTagIds,
    IReadOnlyCollection<Guid> RestrictedTagIds);

/// <summary>
/// The resolved constraint set used to drive AI generation for a single meal slot.
/// Produced by <see cref="MealConstraintResolver.ResolveForGeneration"/>.
/// </summary>
public sealed record GenerationConstraints(
    /// <summary>Attendees for this slot (DefaultAttendees filtered to current members).</summary>
    IReadOnlyList<Guid> EffectiveAttendees,
    /// <summary>Per-attendee hard stances (Required/Restricted). Each attendee appears once.</summary>
    IReadOnlyList<AttendeeHardStances> AttendeeStances,
    /// <summary>
    /// Soft bias weights per tag. Positive = preferred, negative = disliked.
    /// Computed as the average weight across attendees (Preferred=+1, Disliked=-1), normalized to [-1, 1].
    /// Tags with a net weight of 0 are omitted.
    /// </summary>
    IReadOnlyDictionary<Guid, float> PreferredTagWeights)
{
    /// <summary>UNION of Required-stance tag IDs across all attendees — derived from AttendeeStances.</summary>
    public IReadOnlyCollection<Guid> RequiredTagIds =>
        AttendeeStances.SelectMany(a => a.RequiredTagIds).Distinct().ToHashSet();

    /// <summary>UNION of Restricted-stance tag IDs across all attendees — derived from AttendeeStances.</summary>
    public IReadOnlyCollection<Guid> RestrictedTagIds =>
        AttendeeStances.SelectMany(a => a.RestrictedTagIds).Distinct().ToHashSet();

    public static GenerationConstraints Empty { get; } = new(
        EffectiveAttendees: [],
        AttendeeStances: [],
        PreferredTagWeights: new Dictionary<Guid, float>());
}
