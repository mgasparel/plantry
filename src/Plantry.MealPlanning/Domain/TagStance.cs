using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Entity child of <see cref="UserPreference"/>. One stance over one tag (mealplanning.md §tag_stance, C1).
/// Neutral is the ABSENCE of a row — SetStance(Neutral) deletes (M6).
/// Required / Restricted are hard stances (bind the planner); Preferred / Disliked are soft (bias score, M11).
/// </summary>
public sealed class TagStance : Entity<TagStanceId>
{
    // Required by EF
    private TagStance() { }

    public HouseholdId HouseholdId { get; private set; }
    public UserPreferenceId UserPreferenceId { get; private set; }

    /// <summary>Soft ref → recipes.tag (DM-20); UNIQUE (user_preference_id, tag_id).</summary>
    public Guid TagId { get; private set; }

    /// <summary>
    /// 'Required' | 'Preferred' | 'Disliked' | 'Restricted' — no 'Neutral' row (M6).
    /// </summary>
    public string Stance { get; private set; } = default!;
}
