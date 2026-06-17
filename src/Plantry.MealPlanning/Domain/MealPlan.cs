using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Aggregate root — one week's meal plan for a household (mealplanning.md §meal_plan, C2).
/// One plan per (household, week_start); week_start is always a Monday (M8), normalized app-side.
/// Past weeks are retained as the analytics substrate — no archive/delete behaviour (C2).
/// <para>
/// MP-O8: A cell (date × slot) holds an ordered stack of 0..n meals. Each meal has its own
/// Ordinal (1-based, contiguous per cell). New meals append at NextOrdinal. After removal,
/// RenumberCell restores contiguity.
/// </para>
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

    // ── Cell stack queries ────────────────────────────────────────────────────

    /// <summary>
    /// Returns all meals in the cell, ordered by Ordinal (MP-O8).
    /// Returns an empty list when the cell is empty.
    /// </summary>
    public IReadOnlyList<PlannedMeal> MealsInCell(DateOnly date, MealSlotId slotId) =>
        _plannedMeals
            .Where(m => m.Date == date && m.MealSlotId == slotId)
            .OrderBy(m => m.Ordinal)
            .ToList();

    /// <summary>
    /// Finds a planned meal by its ID within this plan.
    /// Returns null when no meal with that ID exists.
    /// </summary>
    public PlannedMeal? FindById(PlannedMealId mealId) =>
        _plannedMeals.FirstOrDefault(m => m.Id == mealId);

    // ── Assign ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assigns a dish-based meal to a cell.
    /// <list type="bullet">
    ///   <item>When <paramref name="mealId"/> is null: appends a new meal at NextOrdinal (MP-O8 append).</item>
    ///   <item>When <paramref name="mealId"/> is set: updates the identified meal's dishes (edit path).</item>
    /// </list>
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
        string? hardStanceWarning = null,
        PlannedMealId? mealId = null)
    {
        ValidateDateInWeek(date);
        if (dishes.Count == 0)
            throw new InvalidOperationException("At least one dish is required (M13).");

        var now = clock.UtcNow;

        if (mealId is not null)
        {
            // Edit existing meal by ID
            var existing = FindById(mealId.Value)
                ?? throw new InvalidOperationException($"No meal with id {mealId} found in plan.");
            existing.UpdateDishes(dishes, createdBy, now);
            existing.SetAttendeesOverride(attendeesOverride, createdBy, now);
            RaiseDomainEvent(new MealPlanned(Id, existing.Id, date, slotId));
        }
        else
        {
            // Append a new meal at the next ordinal in this cell
            var ordinal = NextOrdinal(date, slotId);
            var meal = PlannedMeal.CreateWithDishes(HouseholdId, Id, date, slotId, dishes, attendeesOverride, source, createdBy, now, ordinal);
            _plannedMeals.Add(meal);
            RaiseDomainEvent(new MealPlanned(Id, meal.Id, date, slotId));
        }

        UpdatedAt = now;
        return new AssignMealResult(hardStanceWarning);
    }

    /// <summary>
    /// Assigns a note-based meal to a cell.
    /// <list type="bullet">
    ///   <item>When <paramref name="mealId"/> is null: appends a new note meal at NextOrdinal.</item>
    ///   <item>When <paramref name="mealId"/> is set: updates the identified meal's note (edit path).</item>
    /// </list>
    /// Validates M2 (date in week), M13 (dishes XOR note).
    /// </summary>
    public AssignMealResult AssignNote(
        DateOnly date,
        MealSlotId slotId,
        string note,
        List<Guid>? attendeesOverride,
        string source,
        Guid createdBy,
        IClock clock,
        PlannedMealId? mealId = null)
    {
        ValidateDateInWeek(date);
        if (string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("Note must not be blank (M13).");

        var now = clock.UtcNow;

        if (mealId is not null)
        {
            // Edit existing meal by ID
            var existing = FindById(mealId.Value)
                ?? throw new InvalidOperationException($"No meal with id {mealId} found in plan.");
            existing.UpdateNote(note, createdBy, now);
            existing.SetAttendeesOverride(attendeesOverride, createdBy, now);
            RaiseDomainEvent(new MealPlanned(Id, existing.Id, date, slotId));
        }
        else
        {
            // Append a new note meal at the next ordinal in this cell
            var ordinal = NextOrdinal(date, slotId);
            var meal = PlannedMeal.CreateWithNote(HouseholdId, Id, date, slotId, note, attendeesOverride, source, createdBy, now, ordinal);
            _plannedMeals.Add(meal);
            RaiseDomainEvent(new MealPlanned(Id, meal.Id, date, slotId));
        }

        UpdatedAt = now;
        return new AssignMealResult(null);
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the planned meal identified by <paramref name="mealId"/>. No-op if not found.
    /// After removal, RenumberCell restores contiguous ordinals for the remaining meals in the cell.
    /// </summary>
    public void ClearMeal(PlannedMealId mealId, IClock clock)
    {
        var existing = FindById(mealId);
        if (existing is null) return;

        var date = existing.Date;
        var slotId = existing.MealSlotId;

        _plannedMeals.Remove(existing);
        RenumberCell(date, slotId);
        UpdatedAt = clock.UtcNow;
    }

    // ── Move (relocate, no swap) ──────────────────────────────────────────────

    /// <summary>
    /// Relocates a planned meal from its current cell to the target cell within the same week (MP-O8 / C11).
    /// The meal is appended at the end of the target cell's stack (NextOrdinal).
    /// The source cell is renumbered to keep ordinals contiguous.
    /// The per-instance AttendeesOverride TRAVELS with the meal (M4).
    /// Does NOT re-validate constraints (C12).
    /// There is no swap: if the target is occupied, the mover joins the stack.
    /// </summary>
    public void MoveMeal(
        PlannedMealId mealId,
        DateOnly toDate,
        MealSlotId toSlotId,
        IClock clock)
    {
        ValidateDateInWeek(toDate);

        var mover = FindById(mealId)
            ?? throw new InvalidOperationException($"No meal with id {mealId} to move.");

        var fromDate = mover.Date;
        var fromSlotId = mover.MealSlotId;

        var now = clock.UtcNow;
        var updatedBy = mover.UpdatedBy;

        // Compute destination ordinal before moving so the source cell count is still accurate
        var destOrdinal = NextOrdinal(toDate, toSlotId);

        mover.MoveTo(toDate, toSlotId, updatedBy, now);
        mover.SetOrdinal(destOrdinal);

        // Renumber the source cell (mover has already left it in memory)
        RenumberCell(fromDate, fromSlotId);

        RaiseDomainEvent(new MealMoved(Id, mover.Id, fromDate, fromSlotId, toDate, toSlotId));
        UpdatedAt = now;
    }

    // ── Set attendees ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sets or clears the per-instance attendees override for a planned meal (M4).
    /// </summary>
    public void SetMealAttendees(PlannedMealId mealId, List<Guid>? attendeesOverride, IClock clock)
    {
        var meal = FindById(mealId)
            ?? throw new InvalidOperationException($"No meal with id {mealId}.");

        var now = clock.UtcNow;
        meal.SetAttendeesOverride(attendeesOverride, meal.UpdatedBy, now);
        UpdatedAt = now;
    }

    // ── AI proposal acceptance ────────────────────────────────────────────────

    /// <summary>
    /// Atomically accepts all validated proposals from <paramref name="proposals"/>.
    /// Skips any proposal whose cell already has meals (occupied cells are never touched by AI — MP-O8).
    /// Calls <see cref="AssignMeal"/> with source="ai" for each accepted cell.
    /// </summary>
    /// <returns>The number of cells actually written.</returns>
    public int ApplyProposal(
        IReadOnlyList<ProposedMeal> proposals,
        Guid acceptedBy,
        IClock clock)
    {
        var accepted = 0;
        foreach (var proposal in proposals)
        {
            // Never write to a cell that already has meals (MP-O8)
            if (MealsInCell(proposal.Date, proposal.MealSlotId).Count > 0)
                continue;

            if (proposal.Dishes.Count == 0)
                continue;

            var dishes = proposal.Dishes
                .OrderBy(d => d.Ordinal)
                .Select(d => new DishSpec(DishKind.Recipe, d.RecipeId, d.Servings))
                .ToList();

            AssignMeal(
                proposal.Date,
                proposal.MealSlotId,
                dishes,
                attendeesOverride: null,
                source: "ai",
                createdBy: acceptedBy,
                clock: clock,
                hardStanceWarning: null,
                mealId: null);

            accepted++;
        }
        return accepted;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Returns the next ordinal for appending a meal to the given cell (max+1, or 1 if empty).</summary>
    private int NextOrdinal(DateOnly date, MealSlotId slotId)
    {
        var cellMeals = _plannedMeals.Where(m => m.Date == date && m.MealSlotId == slotId).ToList();
        return cellMeals.Count == 0 ? 1 : cellMeals.Max(m => m.Ordinal) + 1;
    }

    /// <summary>
    /// Renumbers the remaining meals in a cell to 1..n (in Ordinal order) after a removal.
    /// Ensures ordinals are always contiguous.
    /// </summary>
    private void RenumberCell(DateOnly date, MealSlotId slotId)
    {
        var cellMeals = _plannedMeals
            .Where(m => m.Date == date && m.MealSlotId == slotId)
            .OrderBy(m => m.Ordinal)
            .ToList();

        for (int i = 0; i < cellMeals.Count; i++)
            cellMeals[i].SetOrdinal(i + 1);
    }

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
