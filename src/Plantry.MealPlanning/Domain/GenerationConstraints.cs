namespace Plantry.MealPlanning.Domain;

/// <summary>
/// The resolved constraint set used to drive AI generation for a single meal slot.
/// Produced by <see cref="MealConstraintResolver.ResolveForGeneration"/>.
/// </summary>
public sealed record GenerationConstraints(
    /// <summary>Attendees for this slot (DefaultAttendees filtered to current members).</summary>
    IReadOnlyList<Guid> EffectiveAttendees,
    /// <summary>UNION of Required-stance tag IDs across all attendees — AI must include these.</summary>
    IReadOnlyCollection<Guid> RequiredTagIds,
    /// <summary>UNION of Restricted-stance tag IDs across all attendees — AI must never propose these.</summary>
    IReadOnlyCollection<Guid> RestrictedTagIds,
    /// <summary>
    /// Soft bias weights per tag. Positive = preferred, negative = disliked.
    /// Computed as the average weight across attendees (Preferred=+1, Disliked=-1), normalized to [-1, 1].
    /// Tags with a net weight of 0 are omitted.
    /// </summary>
    IReadOnlyDictionary<Guid, float> PreferredTagWeights)
{
    public static GenerationConstraints Empty { get; } = new(
        [],
        new HashSet<Guid>(),
        new HashSet<Guid>(),
        new Dictionary<Guid, float>());
}
