using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Aggregate root holding a household's ordered meal-slot vocabulary (mealplanning.md §meal_slot_config).
/// One config per household; seeded with Breakfast / Lunch / Dinner at household creation (DM-9).
/// Active ordinal contiguity and label uniqueness are app-layer rules enforced here (M9).
/// </summary>
public sealed class MealSlotConfig : AggregateRoot<MealSlotConfigId>
{
    private readonly List<MealSlot> _slots = [];

    // Required by EF
    private MealSlotConfig() { }

    private MealSlotConfig(MealSlotConfigId id, HouseholdId householdId, DateTimeOffset now) : base(id)
    {
        HouseholdId = householdId;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public HouseholdId HouseholdId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<MealSlot> Slots => _slots.AsReadOnly();

    /// <summary>
    /// Creates a new <see cref="MealSlotConfig"/> and seeds the three default slots —
    /// Breakfast, Lunch, Dinner — in ascending ordinal order.
    /// </summary>
    public static MealSlotConfig CreateWithDefaults(HouseholdId householdId, IClock clock)
    {
        var now = clock.UtcNow;
        var config = new MealSlotConfig(MealSlotConfigId.New(), householdId, now);

        config._slots.Add(MealSlot.Create(MealSlotId.New(), householdId, config.Id, "Breakfast", ordinal: 1));
        config._slots.Add(MealSlot.Create(MealSlotId.New(), householdId, config.Id, "Lunch",     ordinal: 2));
        config._slots.Add(MealSlot.Create(MealSlotId.New(), householdId, config.Id, "Dinner",    ordinal: 3));

        return config;
    }
}
