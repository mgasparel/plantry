namespace Plantry.MealPlanning.Application;

/// <summary>
/// Anti-corruption read port onto Recipes + Inventory for the MealPlanning context (plantry-0eut): the
/// Cook strip on the plan card DERIVES each planned dish's cooked/eaten state rather than MealPlanning
/// storing any flag of its own. Implemented in Plantry.Web as the composition-root join over
/// <c>Plantry.Recipes.Domain.ICookEventRepository</c> (recipe dishes: a <c>CookEvent</c> whose
/// <c>PlannedDishId</c> matches) and <c>Plantry.Inventory.Application.IJournalEntriesBySourceRefReader</c>
/// (product dishes: journal movements whose <c>SourceRef</c> matches, netted to a consumed/not-consumed
/// state — plantry-zcbx's "Eat" action is the only writer, landing after this port).
/// </summary>
public interface IMealPlanCookStatusReader
{
    /// <summary>
    /// Batched status lookup for a set of <c>PlannedDish</c> ids — one query per source (recipe
    /// CookEvents, product journal movements), never per-dish. A dish id absent from the result is
    /// still pending; a dish id present is done, with <see cref="DishCookStatus.At"/> the moment it
    /// was completed (recipe: the matching CookEvent's CookedAt; product: the latest journal movement
    /// that left the dish net-consumed).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default);
}

/// <summary>A planned dish's derived "done" state — presence in the result dictionary IS the signal.</summary>
/// <param name="At">When the dish was completed (cooked or eaten).</param>
public sealed record DishCookStatus(DateTimeOffset At);
