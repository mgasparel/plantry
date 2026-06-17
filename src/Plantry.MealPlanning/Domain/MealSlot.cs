using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Entity child of <see cref="MealSlotConfig"/>. Represents one configurable, ordered meal slot
/// (e.g. Breakfast). Stable ID — never physically deleted, only soft-archived — so
/// <c>planned_meal</c>s always remain resolvable (M10).
/// </summary>
public sealed class MealSlot : Entity<MealSlotId>
{
    // Required by EF
    private MealSlot() { }

    private MealSlot(
        MealSlotId id,
        HouseholdId householdId,
        MealSlotConfigId configId,
        string label,
        int ordinal) : base(id)
    {
        HouseholdId = householdId;
        ConfigId = configId;
        Label = label;
        Ordinal = ordinal;
        DefaultAttendees = [];
    }

    public HouseholdId HouseholdId { get; private set; }
    public MealSlotConfigId ConfigId { get; private set; }
    public string Label { get; private set; } = default!;
    public int Ordinal { get; private set; }

    /// <summary>
    /// Members who normally eat this slot. Empty list = nobody (not null). Elements are
    /// identity user_id soft-refs (DM-3 / mealplanning.md resolved call 1).
    /// </summary>
    public List<Guid> DefaultAttendees { get; private set; } = [];

    /// <summary>Null means the slot is active.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsActive => ArchivedAt is null;

    internal static MealSlot Create(
        MealSlotId id,
        HouseholdId householdId,
        MealSlotConfigId configId,
        string label,
        int ordinal) =>
        new(id, householdId, configId, label, ordinal);
}
