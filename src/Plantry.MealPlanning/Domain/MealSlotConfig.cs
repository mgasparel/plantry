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

    /// <summary>
    /// Adds a new active slot at the next ordinal. Label must be non-blank and unique among active slots (M9).
    /// </summary>
    public MealSlot AddSlot(string label, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new InvalidOperationException("Slot label must not be blank.");

        var trimmed = label.Trim();
        var activeSlots = _slots.Where(s => s.IsActive).ToList();
        if (activeSlots.Any(s => s.Label.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A slot named '{trimmed}' already exists.");

        var nextOrdinal = activeSlots.Count == 0 ? 1 : activeSlots.Max(s => s.Ordinal) + 1;
        var slot = MealSlot.Create(MealSlotId.New(), HouseholdId, Id, trimmed, nextOrdinal);
        _slots.Add(slot);
        UpdatedAt = clock.UtcNow;
        return slot;
    }

    /// <summary>
    /// Renames an active slot. Label must be non-blank and unique among active slots (excluding self).
    /// </summary>
    public void RenameSlot(MealSlotId id, string newLabel, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(newLabel))
            throw new InvalidOperationException("Slot label must not be blank.");

        var slot = _slots.FirstOrDefault(s => s.Id == id && s.IsActive)
            ?? throw new InvalidOperationException($"Active slot '{id}' not found in this config.");

        var trimmed = newLabel.Trim();
        var duplicate = _slots.Any(s => s.IsActive && s.Id != id &&
                                        s.Label.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            throw new InvalidOperationException($"A slot named '{trimmed}' already exists.");

        slot.SetLabel(trimmed);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Reorders active slots to match the supplied ordered list of IDs. All supplied IDs must be active
    /// slots belonging to this config, and the list must contain exactly the set of all active slots.
    /// Ordinals are reassigned 1..n in the supplied order (M9).
    /// </summary>
    public void ReorderSlots(IReadOnlyList<MealSlotId> orderedIds, IClock clock)
    {
        var activeSlots = _slots.Where(s => s.IsActive).ToList();
        if (orderedIds.Count != activeSlots.Count)
            throw new InvalidOperationException(
                $"Reorder list contains {orderedIds.Count} IDs but there are {activeSlots.Count} active slots.");

        var activeSet = activeSlots.Select(s => s.Id).ToHashSet();
        foreach (var id in orderedIds)
        {
            if (!activeSet.Contains(id))
                throw new InvalidOperationException($"Slot '{id}' is not an active slot in this config.");
        }

        for (var i = 0; i < orderedIds.Count; i++)
        {
            var slot = activeSlots.First(s => s.Id == orderedIds[i]);
            slot.SetOrdinal(i + 1);
        }

        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Sets the default attendees (user IDs) for the given active slot.
    /// </summary>
    public void SetDefaultAttendees(MealSlotId id, IReadOnlyList<Guid> memberIds, IClock clock)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == id && s.IsActive)
            ?? throw new InvalidOperationException($"Active slot '{id}' not found in this config.");

        slot.SetDefaultAttendees(memberIds);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Soft-archives the slot (sets ArchivedAt). Active ordinals are renumbered 1..n for remaining
    /// active slots so ordinal contiguity is preserved (M9/M10).
    /// </summary>
    public void ArchiveSlot(MealSlotId id, IClock clock)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == id && s.IsActive)
            ?? throw new InvalidOperationException($"Active slot '{id}' not found or already archived.");

        slot.Archive(clock.UtcNow);

        // Renumber remaining active slots so ordinals stay contiguous 1..n
        var remaining = _slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).ToList();
        for (var i = 0; i < remaining.Count; i++)
            remaining[i].SetOrdinal(i + 1);

        UpdatedAt = clock.UtcNow;
    }
}
