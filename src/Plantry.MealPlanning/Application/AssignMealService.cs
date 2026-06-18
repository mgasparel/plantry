using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service for assigning a meal (J5 / J7).
/// Orchestrates: product-dish validation via ICatalogProductReader,
/// dietary constraint warnings via MealConstraintResolver (C9 — warn not block),
/// and MealPlan aggregate mutation.
/// <para>
/// MP-O8: When <c>mealId</c> is null the service appends a new meal at NextOrdinal.
/// When <c>mealId</c> is supplied it updates the identified meal.
/// </para>
/// </summary>
public sealed class AssignMealService(
    IMealPlanRepository mealPlanRepo,
    IMealSlotConfigRepository slotConfigRepo,
    IUserPreferenceRepository prefsRepo,
    IRecipeReadModel recipeReader,
    IMealPlanCatalogProductReader catalogReader,
    MealConstraintResolver constraintResolver,
    IClock clock)
{
    /// <summary>
    /// Assigns a dish-based meal to a cell.
    /// When <paramref name="mealId"/> is null a new meal is appended; when set, the existing meal is updated.
    /// Validates product references; warns on hard stances.
    /// </summary>
    public async Task<AssignMealResult> AssignDishesAsync(
        HouseholdId householdId,
        DateOnly date,
        MealSlotId slotId,
        IReadOnlyList<DishSpec> dishes,
        List<Guid>? attendeesOverride,
        Guid createdBy,
        PlannedMealId? mealId = null,
        CancellationToken ct = default)
    {
        // Validate product dishes exist in catalog
        foreach (var dish in dishes.Where(d => d.Kind == DishKind.Product))
        {
            var exists = await catalogReader.ExistsAsync(dish.ItemId, ct);
            if (!exists)
                throw new InvalidOperationException(
                    $"Product {dish.ItemId} does not exist in the catalog.");
        }

        var plan = await mealPlanRepo.FindOrCreateAsync(householdId, MealPlan.NormalizeToMonday(date), clock, ct);

        // Compute constraint warning (C9 — warn only)
        string? warning = null;
        var slotConfig = await slotConfigRepo.FindByHouseholdAsync(householdId, ct);
        var slot = slotConfig?.Slots.FirstOrDefault(s => s.Id == slotId);
        if (slot is not null)
        {
            // Gather tag IDs from recipes for constraint checking
            var dishTagIds = new List<Guid>();
            foreach (var dish in dishes.Where(d => d.Kind == DishKind.Recipe))
            {
                var recipe = await recipeReader.GetByIdAsync(dish.ItemId, ct);
                if (recipe is not null)
                    dishTagIds.AddRange(recipe.TagIds);
            }

            // Build a minimal PlannedMeal shell to pass to the resolver
            var effectiveAttendees = attendeesOverride ?? slot.DefaultAttendees;
            var allPrefs = new List<UserPreference>();
            foreach (var userId in effectiveAttendees)
            {
                var pref = await prefsRepo.FindByUserIdAsync(userId, ct);
                if (pref is not null) allPrefs.Add(pref);
            }

            // Create a temporary meal shell for the resolver
            var tempMeal = CreateTempMeal(plan.Id, householdId, date, slotId, attendeesOverride);
            var constraints = constraintResolver.Resolve(tempMeal, slot, allPrefs, dishTagIds);
            warning = constraints.HardStanceWarning;
        }

        var result = plan.AssignMeal(date, slotId, dishes, attendeesOverride, "manual", createdBy, clock, warning, mealId);
        await mealPlanRepo.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Assigns a note-based meal to a cell.
    /// When <paramref name="mealId"/> is null a new meal is appended; when set, the existing meal is updated.
    /// </summary>
    public async Task<AssignMealResult> AssignNoteAsync(
        HouseholdId householdId,
        DateOnly date,
        MealSlotId slotId,
        string note,
        List<Guid>? attendeesOverride,
        Guid createdBy,
        PlannedMealId? mealId = null,
        CancellationToken ct = default)
    {
        var plan = await mealPlanRepo.FindOrCreateAsync(householdId, MealPlan.NormalizeToMonday(date), clock, ct);
        var result = plan.AssignNote(date, slotId, note, attendeesOverride, "manual", createdBy, clock, mealId);
        await mealPlanRepo.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Clears the meal identified by <paramref name="mealId"/> from its cell.
    /// No-op if no plan exists or the meal is not found.
    /// </summary>
    public async Task ClearMealAsync(
        HouseholdId householdId,
        DateOnly date,
        PlannedMealId mealId,
        CancellationToken ct = default)
    {
        var plan = await mealPlanRepo.FindByWeekAsync(householdId, MealPlan.NormalizeToMonday(date), ct);
        if (plan is null) return;

        plan.ClearMeal(mealId, clock);
        await mealPlanRepo.SaveChangesAsync(ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static PlannedMeal CreateTempMeal(
        MealPlanId planId,
        HouseholdId householdId,
        DateOnly date,
        MealSlotId slotId,
        List<Guid>? attendeesOverride)
    {
        // We need a PlannedMeal object to pass to the resolver. Use a note-based one
        // as a thin shell — only the AttendeesOverride matters for the resolver.
        return PlannedMeal.CreateWithNote(householdId, planId, date, slotId, "temp", attendeesOverride, "manual",
            Guid.Empty, DateTimeOffset.UtcNow);
    }
}
