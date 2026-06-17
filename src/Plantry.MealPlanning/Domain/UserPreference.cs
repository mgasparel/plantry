using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Aggregate root — one member's dietary profile (mealplanning.md §user_preference, C1, MP-O1).
/// Created lazily on first edit (J3). One profile per (household, user) (M6).
/// Neutral is the ABSENCE of a row — <see cref="SetStance"/> with "Neutral" removes the row (M6).
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

    /// <summary>
    /// Factory: creates a new <see cref="UserPreference"/> for a member. Called lazily on first edit (M6).
    /// </summary>
    public static UserPreference Create(HouseholdId householdId, Guid userId, IClock clock) =>
        new()
        {
            Id = UserPreferenceId.New(),
            HouseholdId = householdId,
            UserId = userId,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };

    /// <summary>
    /// Upserts a stance on <paramref name="tagId"/>. "Neutral" removes the existing row (M6 — Neutral is
    /// the absence of a row). Valid non-neutral values: "Required", "Preferred", "Disliked", "Restricted".
    /// </summary>
    /// <returns><c>true</c> when a row was added/updated; <c>false</c> when a row was removed (Neutral).</returns>
    public bool SetStance(Guid tagId, string stance, IClock clock)
    {
        if (stance == "Neutral")
            return ClearStanceCore(tagId, clock);

        ValidateStance(stance);

        var existing = _tagStances.SingleOrDefault(ts => ts.TagId == tagId);
        if (existing is not null)
            existing.UpdateStance(stance, clock);
        else
            _tagStances.Add(TagStance.Create(HouseholdId, Id, tagId, stance));

        UpdatedAt = clock.UtcNow;
        return true;
    }

    /// <summary>Removes the stance row for <paramref name="tagId"/> (makes it Neutral). No-op if already absent.</summary>
    public bool ClearStance(Guid tagId, IClock clock) => ClearStanceCore(tagId, clock);

    private bool ClearStanceCore(Guid tagId, IClock clock)
    {
        var existing = _tagStances.SingleOrDefault(ts => ts.TagId == tagId);
        if (existing is null) return false;

        _tagStances.Remove(existing);
        UpdatedAt = clock.UtcNow;
        return true;
    }

    private static void ValidateStance(string stance)
    {
        if (stance is not ("Required" or "Preferred" or "Disliked" or "Restricted"))
            throw new ArgumentException(
                $"Invalid stance '{stance}'. Valid values: Required, Preferred, Disliked, Restricted.",
                nameof(stance));
    }
}
