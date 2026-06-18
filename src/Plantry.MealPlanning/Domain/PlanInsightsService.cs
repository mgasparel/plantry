using Plantry.MealPlanning.Application;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Domain service (P3-5): inspects a <see cref="MealPlan"/> and produces advisory
/// <see cref="PlanInsights"/> callouts for the week view rail (J1 step 5 / J10).
///
/// Pure read-side — stateless, never persisted, recomputed on every change.
/// Reuses <see cref="IMealPlanExpiringStockReader"/> and <see cref="PlanCostingService"/>
/// via the ports already defined in MealPlanning.Application; no new domain I/O ports.
///
/// Five rules:
///   1. <see cref="InsightKind.UnusedExpiring"/>  — expiring stock not consumed by any planned dish.
///   2. <see cref="InsightKind.OverBudget"/>       — week cost exceeds the household budget target.
///   3. <see cref="InsightKind.RepetitionThisWeek"/> — same recipe appears 2+ times this week.
///   4. <see cref="InsightKind.RepetitionVsHistory"/> — same recipe appears in a retained prior plan.
///   5. <see cref="InsightKind.UnfilledSlot"/>     — one or more (date × slot) cells have no meal.
///   6. <see cref="InsightKind.HardConflictResolved"/> — a cell has a meal with 2+ dishes (split conflict).
/// </summary>
public sealed class PlanInsightsService(
    IMealPlanExpiringStockReader expiringStockReader,
    IRecipeReadModel recipeReader)
{
    /// <summary>Days-before-expiry threshold for the "Use soon" window (mirrors Recipes/Inventory).</summary>
    public const int ExpiringSoonDays = 4;

    /// <summary>
    /// Inspects <paramref name="plan"/> and returns advisory insights.
    ///
    /// <param name="plan">The current week's plan to inspect.</param>
    /// <param name="allCells">
    /// All (date × slotId) cell keys that exist this week. An absent cell key means an unfilled slot.
    /// </param>
    /// <param name="weekTotalCost">
    /// Pre-computed week cost from <see cref="PlanCostingService"/> (to avoid re-fetching pricing).
    /// Null when no pricing data is available.
    /// </param>
    /// <param name="budgetTarget">
    /// Household budget target for the week; null means "no target" — suppresses the over-budget rule.
    /// </param>
    /// <param name="priorPlans">
    /// Recently retained prior plans (for repetition-vs-history rule). Null or empty suppresses the rule.
    /// </param>
    /// <param name="today">Today's date (used for the expiry window calculation).</param>
    /// <param name="ct">Cancellation token.</param>
    /// </summary>
    public async Task<PlanInsights> InspectAsync(
        MealPlan plan,
        IReadOnlyList<string> allCells,
        decimal? weekTotalCost,
        decimal? budgetTarget,
        IReadOnlyList<MealPlan>? priorPlans,
        DateOnly today,
        CancellationToken ct = default)
    {
        var callouts = new List<PlanInsight>();

        // ── Rule 1: Unused expiring stock ──────────────────────────────────────
        var unusedExpiringInsights = await GetUnusedExpiringAsync(plan, today, ct);
        callouts.AddRange(unusedExpiringInsights);

        // ── Rule 2: Over budget ────────────────────────────────────────────────
        if (budgetTarget is { } budget && budget > 0 && weekTotalCost is { } cost && cost > budget)
        {
            callouts.Add(new PlanInsight(
                InsightKind.OverBudget,
                "warn", "dollar-sign",
                $"Over budget — ${cost:F0} vs ${budget:F0} target",
                "Your planned meals exceed the weekly budget target.",
                ActionUrl: null));
        }

        // ── Rule 3: Repetition this week ──────────────────────────────────────
        var thisWeekRepetitions = GetRepetitionThisWeek(plan);
        callouts.AddRange(thisWeekRepetitions);

        // ── Rule 4: Repetition vs prior plans ─────────────────────────────────
        if (priorPlans is { Count: > 0 })
        {
            var historyRepetitions = GetRepetitionVsHistory(plan, priorPlans);
            callouts.AddRange(historyRepetitions);
        }

        // ── Rule 5: Unfilled slots ─────────────────────────────────────────────
        var filledCells = plan.PlannedMeals
            .Select(m => $"{m.Date:yyyy-MM-dd}_{m.MealSlotId.Value:N}")
            .ToHashSet(StringComparer.Ordinal);

        var emptyCellCount = allCells.Count(c => !filledCells.Contains(c));
        if (emptyCellCount > 0)
        {
            callouts.Add(new PlanInsight(
                InsightKind.UnfilledSlot,
                "info", "calendar",
                $"{emptyCellCount} slot{(emptyCellCount == 1 ? "" : "s")} still open this week",
                "Auto-fill the gaps with AI suggestions, or add meals by hand.",
                ActionUrl: null));
        }

        // ── Rule 6: Hard conflict resolved (split meals) ───────────────────────
        foreach (var meal in plan.PlannedMeals.Where(m => m.PlannedDishes.Count >= 2))
        {
            callouts.Add(new PlanInsight(
                InsightKind.HardConflictResolved,
                "info", "layers",
                $"Split meal on {meal.Date:ddd MMM d}",
                "A meal was split into multiple dishes in the same slot — a conflict was resolved by combining them.",
                ActionUrl: null));
        }

        return new PlanInsights(callouts.AsReadOnly());
    }

    // ── Rule 1 implementation ─────────────────────────────────────────────────

    private async Task<IReadOnlyList<PlanInsight>> GetUnusedExpiringAsync(
        MealPlan plan,
        DateOnly today,
        CancellationToken ct)
    {
        // Get all product IDs whose stock is expiring soon.
        var expiringProductIds = await expiringStockReader.GetExpiringProductIdsAsync(today, ExpiringSoonDays, ct);
        if (expiringProductIds.Count == 0) return [];

        // Collect all recipe IDs planned this week.
        var plannedRecipeIds = plan.PlannedMeals
            .SelectMany(m => m.PlannedDishes)
            .Where(d => d.RecipeId.HasValue)
            .Select(d => d.RecipeId!.Value)
            .Distinct()
            .ToList();

        // Also collect all product IDs directly planned (product dishes).
        var plannedProductIds = plan.PlannedMeals
            .SelectMany(m => m.PlannedDishes)
            .Where(d => d.ProductId.HasValue)
            .Select(d => d.ProductId!.Value)
            .ToHashSet();

        // Determine once (not per-product) whether any planned recipe uses expiring ingredients.
        // Since IRecipeReadModel doesn't expose per-product ingredient lists, we use
        // HasExpiringIngredients from enrichment as a proxy: if ANY planned recipe has
        // expiring ingredients (given today's date), that expiring stock is consumed.
        // The proxy is intentionally coarse; a per-product-exact check needs a new ACL port
        // (tracked as a follow-up bead). Computing this once avoids N×M GetEnrichmentAsync calls.
        bool anyRecipeUsesExpiring = false;
        foreach (var recipeId in plannedRecipeIds)
        {
            var enrichment = await recipeReader.GetEnrichmentAsync(recipeId, 1, today, ct);
            if (enrichment?.HasExpiringIngredients == true)
            {
                anyRecipeUsesExpiring = true;
                break;
            }
        }

        // For each expiring product, emit a callout only if it is not directly planned
        // and no planned recipe is consuming any expiring ingredient.
        var unusedExpiringIds = new List<Guid>();
        foreach (var productId in expiringProductIds)
        {
            if (plannedProductIds.Contains(productId) || anyRecipeUsesExpiring) continue;
            unusedExpiringIds.Add(productId);
        }

        if (unusedExpiringIds.Count == 0) return [];

        // We don't have product names in this domain layer (cross-context lookup would violate
        // the bounded context boundary). Emit a single aggregate callout instead of per-product.
        var n = unusedExpiringIds.Count;
        return
        [
            new PlanInsight(
                InsightKind.UnusedExpiring,
                "warn", "clock",
                $"{n} expiring item{(n == 1 ? "" : "s")} not used this week",
                "Some stock expires soon but isn't on this week's menu. Use it up before it goes to waste.",
                ActionUrl: "/Recipes?filter=use-soon")
        ];
    }

    // ── Rule 3 implementation ─────────────────────────────────────────────────

    private static IReadOnlyList<PlanInsight> GetRepetitionThisWeek(MealPlan plan)
    {
        // Count how many times each recipe appears this week (by recipe ID for accuracy).
        var recipeCounts = new Dictionary<Guid, int>();
        foreach (var meal in plan.PlannedMeals)
        {
            foreach (var dish in meal.PlannedDishes.Where(d => d.RecipeId.HasValue))
            {
                var id = dish.RecipeId!.Value;
                recipeCounts[id] = recipeCounts.GetValueOrDefault(id) + 1;
            }
        }

        var insights = new List<PlanInsight>();
        foreach (var (_, count) in recipeCounts.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value))
        {
            var times = count == 2 ? "twice" : $"{count} times";
            insights.Add(new PlanInsight(
                InsightKind.RepetitionThisWeek,
                "info", "swap",
                $"A recipe is planned {times} this week",
                "Variety's up to you — repeats are fine if you're batch-cooking.",
                ActionUrl: null));
        }

        return insights;
    }

    // ── Rule 4 implementation ─────────────────────────────────────────────────

    private static IReadOnlyList<PlanInsight> GetRepetitionVsHistory(
        MealPlan plan,
        IReadOnlyList<MealPlan> priorPlans)
    {
        // Recipe IDs in the current week.
        var thisWeekRecipeIds = plan.PlannedMeals
            .SelectMany(m => m.PlannedDishes)
            .Where(d => d.RecipeId.HasValue)
            .Select(d => d.RecipeId!.Value)
            .ToHashSet();

        if (thisWeekRecipeIds.Count == 0) return [];

        // Recipe IDs in prior plans.
        var priorRecipeIds = priorPlans
            .SelectMany(p => p.PlannedMeals)
            .SelectMany(m => m.PlannedDishes)
            .Where(d => d.RecipeId.HasValue)
            .Select(d => d.RecipeId!.Value)
            .ToHashSet();

        var repeated = thisWeekRecipeIds.Intersect(priorRecipeIds).Count();
        if (repeated == 0) return [];

        return
        [
            new PlanInsight(
                InsightKind.RepetitionVsHistory,
                "info", "swap",
                $"{repeated} recipe{(repeated == 1 ? "" : "s")} from recent weeks repeated",
                "These recipes also appeared in recent weeks. Consider mixing in something new.",
                ActionUrl: null)
        ];
    }
}

// ── Output types ──────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Categorises an insight so consumers can route to the right action or filter by kind.
/// </summary>
public enum InsightKind
{
    /// <summary>Expiring stock that no planned dish will consume.</summary>
    UnusedExpiring,

    /// <summary>Weekly meal cost exceeds the household budget target.</summary>
    OverBudget,

    /// <summary>Same recipe appears 2+ times in the current week's plan.</summary>
    RepetitionThisWeek,

    /// <summary>Recipe from the current plan also appears in a retained prior week.</summary>
    RepetitionVsHistory,

    /// <summary>One or more (date × slot) cells have no meal assigned.</summary>
    UnfilledSlot,

    /// <summary>A cell has a meal with 2+ dishes — a hard conflict was resolved by splitting.</summary>
    HardConflictResolved,
}

/// <summary>
/// A single advisory callout produced by <see cref="PlanInsightsService.InspectAsync"/>.
/// </summary>
/// <param name="Kind">Categorisation for routing and filtering.</param>
/// <param name="Tone">CSS data-tone attribute: "warn" | "info" | "good".</param>
/// <param name="Icon">Icon name (maps to SVG sprite suffix in the view).</param>
/// <param name="Title">Short heading text.</param>
/// <param name="Body">Explanatory sentence shown below the title.</param>
/// <param name="ActionUrl">
/// Optional hyperlink target for the callout's action button (e.g. "/Recipes?filter=use-soon").
/// Null when there is no action.
/// </param>
public sealed record PlanInsight(
    InsightKind Kind,
    string Tone,
    string Icon,
    string Title,
    string Body,
    string? ActionUrl);

/// <summary>The complete set of insights produced for one week's plan.</summary>
/// <param name="Insights">Ordered list of advisory callouts (never null, may be empty).</param>
public sealed record PlanInsights(IReadOnlyList<PlanInsight> Insights)
{
    /// <summary>True when no insights were found — the plan is "clean".</summary>
    public bool IsClean => Insights.Count == 0;
}
