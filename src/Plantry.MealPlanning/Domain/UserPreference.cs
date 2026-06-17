using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Aggregate root — one member's dietary profile (mealplanning.md §user_preference, C1, MP-O1).
/// Created lazily on first edit (J3). One profile per (household, user) (M6).
/// </summary>
public sealed class UserPreference : AggregateRoot<UserPreferenceId>
{
    private readonly List<TagStance> _tagStances = [];

    // Required by EF
    private UserPreference() { }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>The member (soft-ref → identity user); UNIQUE (household_id, user_id).</summary>
    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<TagStance> TagStances => _tagStances.AsReadOnly();
}
