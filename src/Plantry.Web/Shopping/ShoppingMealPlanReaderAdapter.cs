using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
using Plantry.Shopping.Application;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-layer adapter implementing <see cref="IShoppingMealPlanReader"/> over the Meal Planning bounded
/// context's <see cref="MealPlanningDbContext"/>. This is the anti-corruption seam between Shopping and
/// Meal Planning — Shopping never takes a direct dependency on Meal Planning's EF context (ADR-002,
/// ADR-010 <c>MP → SHOP</c>). Follows the same adapter shape as <c>ShoppingRecipeReaderAdapter</c>
/// (Shopping → Recipes ACL) and <c>MealPlanCatalogProductReaderAdapter</c>.
///
/// <para>
/// Resolves a MealPlan contribution's <c>SourceRef</c> — a <c>planned_meal</c> (slot/entry) id — to the
/// slot's weekday (from the planned meal's date) and meal-slot label (e.g. "Dinner"). The EF query filter
/// and Postgres RLS interceptor (ADR-008) scope both reads to the current household automatically. A
/// SourceRef that is not a resolvable slot id (e.g. a coarser whole-plan ref, a deleted slot, or a foreign
/// household's) is simply absent from the result, so the query service falls back to "for your meal plan".
/// </para>
/// </summary>
public sealed class ShoppingMealPlanReaderAdapter(MealPlanningDbContext db) : IShoppingMealPlanReader
{
    public async Task<IReadOnlyDictionary<Guid, ShoppingMealPlanSlot>> GetMealPlanSlotsAsync(
        IReadOnlyList<Guid> slotRefs,
        CancellationToken ct = default)
    {
        if (slotRefs.Count == 0)
            return new Dictionary<Guid, ShoppingMealPlanSlot>();

        // Match on the strongly-typed key: EF cannot translate a .Value access on a converted value-object
        // key combined with the converted-key household query filter (same constraint as the sibling adapters).
        var wanted = slotRefs.Select(PlannedMealId.From).ToHashSet();
        var meals = await db.PlannedMeals
            .Where(pm => wanted.Contains(pm.Id))
            .Select(pm => new { pm.Id, pm.Date, pm.MealSlotId })
            .ToListAsync(ct);

        if (meals.Count == 0)
            return new Dictionary<Guid, ShoppingMealPlanSlot>();

        // Resolve the label for each referenced slot in one batch. MealSlot is never physically deleted
        // (only soft-archived), so a planned meal's slot always resolves (M10).
        var slotIds = meals.Select(m => m.MealSlotId).ToHashSet();
        var labels = await db.MealSlots
            .Where(ms => slotIds.Contains(ms.Id))
            .Select(ms => new { ms.Id, ms.Label })
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct);

        var result = new Dictionary<Guid, ShoppingMealPlanSlot>();
        foreach (var m in meals)
        {
            // Omit an entry whose slot label cannot be resolved so the caller falls back gracefully.
            if (labels.TryGetValue(m.MealSlotId, out var label))
                result[m.Id.Value] = new ShoppingMealPlanSlot(m.Date.DayOfWeek, label);
        }

        return result;
    }
}
