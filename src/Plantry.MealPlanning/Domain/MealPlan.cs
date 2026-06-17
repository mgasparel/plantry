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
}
