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
        IncludeInAutoPlan = true;
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

    /// <summary>
    /// Whether this slot is included in <b>bulk</b> AI auto-planning (whole-week Generate and the
    /// "just today" scope). Defaults to <c>true</c> (opt-out model). Explicit per-cell Auto-fill /
    /// Regenerate on a specific cell ignore this flag — the user pointed at that cell (plantry-av8z
    /// decision 1). Manual planning is always unaffected.
    /// </summary>
    public bool IncludeInAutoPlan { get; private set; }

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

    internal void SetLabel(string label) => Label = label;

    internal void SetOrdinal(int ordinal) => Ordinal = ordinal;

    internal void SetDefaultAttendees(IReadOnlyList<Guid> memberIds) =>
        DefaultAttendees = [..memberIds];

    internal void SetIncludeInAutoPlan(bool included) => IncludeInAutoPlan = included;

    internal void Archive(DateTimeOffset now) => ArchivedAt = now;
}
