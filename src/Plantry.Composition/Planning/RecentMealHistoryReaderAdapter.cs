using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Planning.Infrastructure;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Composition-root join over retained Planning rows and Recipes cook events. It returns a transient,
/// Planning-shaped snapshot only; no history is copied into Planning persistence.
/// </summary>
public sealed class RecentMealHistoryReaderAdapter(
    PlanningDbContext planningDb,
    RecipesDbContext recipesDb,
    IClock clock) : IRecentMealHistoryReader
{
    public async Task<RecentMealHistorySnapshot> ReadAsync(
        HouseholdId householdId,
        DateOnly asOfDate,
        DateOnly excludedWeekStart,
        CancellationToken ct = default)
    {
        var earliestDate = RecentMealHistoryPolicy.EarliestRetainedDate(asOfDate);

        // Retained plan rows are bounded by the approved policy and exclude the plan being inspected or
        // generated. Its accepted meals are supplied separately as the stronger current-week input.
        var plannedRows = await (
            from dish in planningDb.PlannedDishes
            join meal in planningDb.PlannedMeals on dish.PlannedMealId equals meal.Id
            join plan in planningDb.MealPlans on meal.MealPlanId equals plan.Id
            where plan.HouseholdId == householdId
                && meal.Date >= earliestDate
                && meal.Date <= asOfDate
                && plan.WeekStart != excludedWeekStart
                && dish.RecipeId != null
            select new PlannedOccurrenceRow(dish.Id.Value, dish.RecipeId!.Value, meal.Date))
            .ToListAsync(ct);

        var plannedDishIds = plannedRows.Select(row => row.PlannedDishId).ToList();
        var excludedWeekDishIds = await (
            from dish in planningDb.PlannedDishes
            join meal in planningDb.PlannedMeals on dish.PlannedMealId equals meal.Id
            join plan in planningDb.MealPlans on meal.MealPlanId equals plan.Id
            where plan.HouseholdId == householdId && plan.WeekStart == excludedWeekStart
            select dish.Id.Value)
            .ToListAsync(ct);
        var utcStart = StartOfLocalDay(earliestDate, clock.Zone);
        var utcEnd = StartOfLocalDay(asOfDate.AddDays(1), clock.Zone);

        // Linked cook events are loaded even when their timestamp falls outside the horizon: the link
        // suppresses the retained planned date because actual CookedAt is authoritative. In-horizon
        // direct cook events are loaded independently and remain distinct real occurrences.
        var cookRows = await recipesDb.CookEvents
            .Where(cook => cook.HouseholdId == householdId
                && !(cook.PlannedDishId != null && excludedWeekDishIds.Contains(cook.PlannedDishId.Value))
                && ((cook.CookedAt >= utcStart && cook.CookedAt < utcEnd)
                    || (cook.PlannedDishId != null && plannedDishIds.Contains(cook.PlannedDishId.Value))))
            .Select(cook => new CookOccurrenceRow(
                cook.RecipeId.Value,
                cook.CookedAt,
                cook.PlannedDishId))
            .ToListAsync(ct);

        var linkedPlannedDishIds = cookRows
            .Where(row => row.PlannedDishId.HasValue)
            .Select(row => row.PlannedDishId!.Value)
            .ToHashSet();

        var occurrences = new List<OccurrenceRow>();
        occurrences.AddRange(plannedRows
            .Where(row => !linkedPlannedDishIds.Contains(row.PlannedDishId))
            .Select(row => new OccurrenceRow(
                row.RecipeId,
                row.OccurredOn,
                RecentMealOccurrenceSource.RetainedPlan,
                CookedAt: null)));

        occurrences.AddRange(cookRows
            .Select(row => new
            {
                Row = row,
                OccurredOn = clock.ToLocalDate(row.CookedAt),
            })
            .Where(item => RecentMealHistoryPolicy.IsRetained(item.OccurredOn, asOfDate))
            .Select(item => new OccurrenceRow(
                item.Row.RecipeId,
                item.OccurredOn,
                RecentMealOccurrenceSource.CookEvent,
                item.Row.CookedAt)));

        if (occurrences.Count == 0) return RecentMealHistorySnapshot.Empty;

        var recipeIds = occurrences.Select(row => RecipeId.From(row.RecipeId)).Distinct().ToList();
        var recipes = await recipesDb.Recipes
            .Where(recipe => recipe.HouseholdId == householdId && recipeIds.Contains(recipe.Id))
            .Select(recipe => new RecipeIdentityRow(
                recipe.Id.Value,
                recipe.Name,
                recipe.ArchivedAt != null))
            .ToListAsync(ct);

        // Resolve named/category facets independently of candidate discovery. Archived recipes and tags
        // remain resolvable historical facts, but neither query can make them selectable candidates.
        var facetRows = await (
            from recipeTag in recipesDb.RecipeTags
            join tag in recipesDb.Tags on recipeTag.TagId equals tag.Id
            where recipeTag.HouseholdId == householdId && recipeIds.Contains(recipeTag.RecipeId)
            select new
            {
                RecipeId = recipeTag.RecipeId.Value,
                TagId = tag.Id.Value,
                tag.Name,
                tag.Category,
            })
            .ToListAsync(ct);

        var facetsByRecipe = facetRows
            .GroupBy(row => row.RecipeId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RecentRecipeFacet>)group
                    .OrderBy(row => row.Category?.ToString())
                    .ThenBy(row => row.Name)
                    .Select(row => new RecentRecipeFacet(
                        row.TagId,
                        row.Name,
                        row.Category?.ToString()))
                    .ToList());

        var occurrencesByRecipe = occurrences
            .GroupBy(row => row.RecipeId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var history = recipes
            .Where(recipe => occurrencesByRecipe.ContainsKey(recipe.RecipeId))
            .Select(recipe => new RecentRecipeHistory(
                recipe.RecipeId,
                recipe.Name,
                recipe.IsArchived,
                occurrencesByRecipe[recipe.RecipeId]
                    .OrderByDescending(row => row.OccurredOn)
                    .ThenByDescending(row => row.CookedAt)
                    .Select(row => new RecentMealOccurrence(
                        row.OccurredOn,
                        row.Source,
                        RecentMealHistoryPolicy.WeightFor(row.OccurredOn, asOfDate),
                        row.CookedAt))
                    .ToList(),
                facetsByRecipe.GetValueOrDefault(recipe.RecipeId, [])))
            .OrderByDescending(recipe => recipe.RecencyScore)
            .ThenBy(recipe => recipe.Name)
            .ToList();

        return new RecentMealHistorySnapshot(history);
    }

    private static DateTimeOffset StartOfLocalDay(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed record PlannedOccurrenceRow(Guid PlannedDishId, Guid RecipeId, DateOnly OccurredOn);
    private sealed record CookOccurrenceRow(Guid RecipeId, DateTimeOffset CookedAt, Guid? PlannedDishId);
    private sealed record OccurrenceRow(
        Guid RecipeId,
        DateOnly OccurredOn,
        RecentMealOccurrenceSource Source,
        DateTimeOffset? CookedAt);
    private sealed record RecipeIdentityRow(Guid RecipeId, string Name, bool IsArchived);
}
