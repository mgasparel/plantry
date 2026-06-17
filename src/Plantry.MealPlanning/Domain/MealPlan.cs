using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Aggregate root — one week's meal plan for a household (mealplanning.md §meal_plan, C2).
/// One plan per (household, week_start); week_start is always a Monday (M8), normalized app-side.
/// Past weeks are retained as the analytics substrate — no archive/delete behaviour (C2).
/// </summary>
public sealed class MealPlan : AggregateRoot<MealPlanId>
{
    private readonly List<PlannedMeal> _plannedMeals = [];

    // Required by EF
    private MealPlan() { }

    private MealPlan(MealPlanId id, HouseholdId householdId, DateOnly weekStart, DateTimeOffset now) : base(id)
    {
        HouseholdId = householdId;
        WeekStart = weekStart;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>ISO-week Monday; normalized to Monday app-side before persist (M8).</summary>
    public DateOnly WeekStart { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<PlannedMeal> PlannedMeals => _plannedMeals.AsReadOnly();

    /// <summary>Creates a new empty week plan, normalizing <paramref name="anyDayInWeek"/> to the Monday.</summary>
    public static MealPlan Start(HouseholdId householdId, DateOnly anyDayInWeek, IClock clock)
    {
        var monday = NormalizeToMonday(anyDayInWeek);
        return new MealPlan(MealPlanId.New(), householdId, monday, clock.UtcNow);
    }

    /// <summary>Returns the Monday of the ISO week containing <paramref name="date"/> (M8).</summary>
    public static DateOnly NormalizeToMonday(DateOnly date)
    {
        // DayOfWeek: Sunday=0, Monday=1 … Saturday=6
        var offset = ((int)date.DayOfWeek + 6) % 7; // days since Monday
        return date.AddDays(-offset);
    }

    /// <summary>
    /// Assigns a dish-based meal to a cell. Creates a new PlannedMeal or replaces the existing one.
    /// Validates M2 (date in week), M3 (servings >= 1), M13 (dishes XOR note).
    /// Returns a warning string if any hard stance is violated (C9 — warn, do not block).
    /// </summary>
    public AssignMealResult AssignMeal(
        DateOnly date,
        MealSlotId slotId,
        IReadOnlyList<DishSpec> dishes,
        List<Guid>? attendeesOverride,
        string source,
        Guid createdBy,
        IClock clock,
        string? hardStanceWarning = null)
    {
        ValidateDateInWeek(date);
        if (dishes.Count == 0)
            throw new InvalidOperationException("At least one dish is required (M13).");

        var now = clock.UtcNow;
        var existing = FindMeal(date, slotId);
        if (existing is null)
        {
            var meal = PlannedMeal.CreateWithDishes(HouseholdId, Id, date, slotId, dishes, attendeesOverride, source, createdBy, now);
            _plannedMeals.Add(meal);
            RaiseDomainEvent(new MealPlanned(Id, meal.Id, date, slotId));
        }
        else
        {
            existing.UpdateDishes(dishes, createdBy, now);
            existing.SetAttendeesOverride(attendeesOverride, createdBy, now);
            RaiseDomainEvent(new MealPlanned(Id, existing.Id, date, slotId));
        }

        UpdatedAt = now;
        return new AssignMealResult(hardStanceWarning);
    }

    /// <summary>
    /// Assigns a note-based meal to a cell. Creates a new PlannedMeal or replaces the existing one.
    /// Validates M2 (date in week), M13 (dishes XOR note).
    /// </summary>
    public AssignMealResult AssignNote(
        DateOnly date,
        MealSlotId slotId,
        string note,
        List<Guid>? attendeesOverride,
        string source,
        Guid createdBy,
        IClock clock)
    {
        ValidateDateInWeek(date);
        if (string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("Note must not be blank (M13).");

        var now = clock.UtcNow;
        var existing = FindMeal(date, slotId);
        if (existing is null)
        {
            var meal = PlannedMeal.CreateWithNote(HouseholdId, Id, date, slotId, note, attendeesOverride, source, createdBy, now);
            _plannedMeals.Add(meal);
            RaiseDomainEvent(new MealPlanned(Id, meal.Id, date, slotId));
        }
        else
        {
            existing.UpdateNote(note, createdBy, now);
            existing.SetAttendeesOverride(attendeesOverride, createdBy, now);
            RaiseDomainEvent(new MealPlanned(Id, existing.Id, date, slotId));
        }

        UpdatedAt = now;
        return new AssignMealResult(null);
    }

    /// <summary>
    /// Removes the planned meal at the given cell. No-op if the cell is empty.
    /// </summary>
    public void ClearMeal(DateOnly date, MealSlotId slotId, IClock clock)
    {
        ValidateDateInWeek(date);
        var existing = FindMeal(date, slotId);
        if (existing is null) return;

        _plannedMeals.Remove(existing);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Relocates a planned meal from one cell to another within the same week (C11).
    /// If the target is occupied, the two meals are swapped.
    /// The per-instance AttendeesOverride TRAVELS with each meal (M4).
    /// Does NOT re-validate constraints (C12).
    /// </summary>
    public void MoveMeal(
        DateOnly fromDate, MealSlotId fromSlotId,
        DateOnly toDate, MealSlotId toSlotId,
        IClock clock)
    {
        ValidateDateInWeek(fromDate);
        ValidateDateInWeek(toDate);

        var mover = FindMeal(fromDate, fromSlotId)
            ?? throw new InvalidOperationException($"No meal at ({fromDate}, {fromSlotId}) to move.");

        var target = FindMeal(toDate, toSlotId);
        var now = clock.UtcNow;
        var updatedBy = mover.UpdatedBy;

        if (target is null)
        {
            // Relocate onto empty cell
            mover.MoveTo(toDate, toSlotId, updatedBy, now);
            RaiseDomainEvent(new MealMoved(Id, mover.Id, fromDate, fromSlotId, toDate, toSlotId));
        }
        else
        {
            // Swap with occupied cell (override travels with each meal — M4)
            mover.MoveTo(toDate, toSlotId, updatedBy, now);
            target.MoveTo(fromDate, fromSlotId, target.UpdatedBy, now);
            RaiseDomainEvent(new MealMoved(Id, mover.Id, fromDate, fromSlotId, toDate, toSlotId, target.Id));
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Sets or clears the per-instance attendees override for a planned meal (M4).
    /// </summary>
    public void SetMealAttendees(DateOnly date, MealSlotId slotId, List<Guid>? attendeesOverride, IClock clock)
    {
        ValidateDateInWeek(date);
        var meal = FindMeal(date, slotId)
            ?? throw new InvalidOperationException($"No meal at ({date}, {slotId}).");

        var now = clock.UtcNow;
        meal.SetAttendeesOverride(attendeesOverride, meal.UpdatedBy, now);
        UpdatedAt = now;
    }

    // ── public lookup (used by MoveMealService for swap pre-check) ───────────────

    /// <summary>
    /// Returns the planned meal at the given cell, or null if the cell is empty.
    /// Used by <see cref="Plantry.MealPlanning.Application.MoveMealService"/> to decide
    /// between a simple relocate and a swap before issuing raw SQL for the swap case.
    /// </summary>
    public PlannedMeal? FindMealPublic(DateOnly date, MealSlotId slotId) =>
        FindMeal(date, slotId);

    /// <summary>
    /// Records the domain event for a swap that was executed via raw SQL (bypassing EF change
    /// tracking). Does NOT mutate any entity state — the SQL already committed both UPDATEs.
    /// </summary>
    public void RecordSwap(
        PlannedMealId moverId, DateOnly fromDate, MealSlotId fromSlotId,
        PlannedMealId targetId, DateOnly toDate, MealSlotId toSlotId)
    {
        RaiseDomainEvent(new MealMoved(Id, moverId, fromDate, fromSlotId, toDate, toSlotId, targetId));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private PlannedMeal? FindMeal(DateOnly date, MealSlotId slotId) =>
        _plannedMeals.FirstOrDefault(m => m.Date == date && m.MealSlotId == slotId);

    private void ValidateDateInWeek(DateOnly date)
    {
        var weekEnd = WeekStart.AddDays(6);
        if (date < WeekStart || date > weekEnd)
            throw new InvalidOperationException(
                $"Date {date} is outside the week [{WeekStart}..{weekEnd}] (M2).");
    }
}

/// <summary>Result of an AssignMeal or AssignNote call.</summary>
public sealed record AssignMealResult(
    /// <summary>Non-null when a hard dietary stance would be violated. Does not block save (C9).</summary>
    string? HardStanceWarning);
