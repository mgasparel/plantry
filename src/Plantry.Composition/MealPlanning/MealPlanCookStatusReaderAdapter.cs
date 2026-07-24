using Plantry.Inventory.Application;
using Plantry.MealPlanning.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Composition-root adapter for <see cref="IMealPlanCookStatusReader"/> (plantry-0eut) — the plan card's
/// Cook strip derives cooked/eaten state without MealPlanning storing anything. Joins Recipes
/// (<see cref="ICookEventRepository"/>: a recipe dish is done when a <c>CookEvent</c> carries its
/// <c>PlannedDishId</c>) and Inventory (<see cref="IJournalEntriesBySourceRefReader"/>: a product dish is
/// done when its net journal movement, keyed by <c>SourceRef</c> = the plan dish id, is negative — the
/// consuming write plantry-zcbx's Eat action stamps, netted against any compensating undo). Neither
/// context depends on the other or on MealPlanning (Gate 2) — this adapter is the only place that knows
/// both halves of the story, exactly the seam <c>StockProvenanceReaderAdapter</c> plays for the pantry
/// History provenance chip.
/// </summary>
public sealed class MealPlanCookStatusReaderAdapter(
    ICookEventRepository cookEvents,
    IJournalEntriesBySourceRefReader journal,
    ITenantContext tenant) : IMealPlanCookStatusReader
{
    public async Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, DishCookStatus>();
        if (plannedDishIds.Count == 0 || tenant.HouseholdId is null)
            return result;

        // Recipe dishes: a CookEvent whose PlannedDishId matches — done at CookedAt.
        var cookedAtByDish = await cookEvents.GetLatestCookedAtByPlannedDishIdsAsync(plannedDishIds, ct);
        foreach (var (dishId, cookedAt) in cookedAtByDish)
            result[dishId] = new DishCookStatus(cookedAt);

        // Product dishes: net the journal movements keyed by SourceRef = the plan dish id. A negative
        // net (consume not fully offset by a compensating undo ADD) means the dish is eaten, timestamped
        // at the most recent consuming movement — so a later re-eat (after an undo) reports its own time,
        // not the original eat's. Today, before plantry-zcbx lands, no writer ever stamps this SourceRef,
        // so every dish resolves to nothing here — the shape simply tolerates that absence.
        var movementsByDish = await journal.ListBySourceRefsAsync(plannedDishIds, ct);
        foreach (var (dishId, movements) in movementsByDish)
        {
            var net = movements.Sum(m => m.Delta);
            if (net >= 0)
                continue; // fully undone, or never net-consumed — still pending

            var latestConsumeAt = movements.Where(m => m.Delta < 0).Max(m => m.OccurredAt);
            result[dishId] = new DishCookStatus(latestConsumeAt);
        }

        return result;
    }
}
