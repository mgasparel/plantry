using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service that drives the AI generate-plan flow (P3-6a, J7).
/// Orchestrates: load week plan → find empty cells → resolve constraints →
/// load candidates → call IMealPlanner (untrusted) → validate via ProposalAcl → stage in IPendingProposalStore.
/// The AI output is never persisted directly — it goes to the pending store for user review.
/// </summary>
public sealed class GeneratePlanService(
    IMealPlanner planner,
    IMealPlanRepository mealPlanRepo,
    IMealSlotConfigRepository slotConfigRepo,
    IUserPreferenceRepository prefsRepo,
    IRecipeReadModel recipeReader,
    IPendingProposalStore proposalStore,
    MealConstraintResolver constraintResolver)
{
    /// <summary>
    /// Finds all empty cells in the given week (or just <paramref name="scopeDate"/> if provided),
    /// requests AI proposals, validates them, and stages the validated proposals in the pending store.
    /// </summary>
    /// <param name="scopeDate">
    /// When non-null, only fills empty cells on this specific date (C13 "just today" scope).
    /// When null, fills all empty cells across the whole week (default).
    /// </param>
    public async Task<GeneratePlanResult> ExecuteAsync(
        HouseholdId householdId,
        DateOnly weekStart,
        string storeKey,
        PlanningWeights? weights,
        DateOnly? scopeDate = null,
        CancellationToken ct = default)
    {
        var monday = Domain.MealPlan.NormalizeToMonday(weekStart);
        var effectiveWeights = weights ?? PlanningWeights.Default;

        // 1. Load existing plan (may be null — no existing meals)
        var plan = await mealPlanRepo.FindByWeekAsync(householdId, monday, ct);

        // 2. Load slot config, find active slots
        var slotConfig = await slotConfigRepo.FindByHouseholdAsync(householdId, ct);
        if (slotConfig is null)
            return new GeneratePlanResult(0, 0);

        var activeSlots = slotConfig.Slots
            .Where(s => s.IsActive)
            .OrderBy(s => s.Ordinal)
            .ToList();

        if (activeSlots.Count == 0)
            return new GeneratePlanResult(0, 0);

        // 3. Load all household member preferences
        var allPrefs = new List<UserPreference>();
        // Collect all unique user IDs across all slot default attendees
        var allUserIds = activeSlots
            .SelectMany(s => s.DefaultAttendees)
            .Distinct()
            .ToList();

        foreach (var userId in allUserIds)
        {
            var pref = await prefsRepo.FindByUserIdAsync(userId, ct);
            if (pref is not null) allPrefs.Add(pref);
        }

        // 4. Find empty cells, honouring PlanningScope (C13).
        // scopeDate != null → "just today" scope: only fill cells on that specific date.
        // scopeDate == null → "whole week" scope: fill all empty cells across 7 days.
        var occupiedKeys = plan?.PlannedMeals
            .Select(m => CellKey(m.Date, m.MealSlotId))
            .ToHashSet() ?? new HashSet<string>();

        var emptyCells = new List<(DateOnly Date, MealSlot Slot)>();
        for (var i = 0; i < 7; i++)
        {
            var date = monday.AddDays(i);
            if (scopeDate.HasValue && date != scopeDate.Value)
                continue; // skip days outside the requested scope
            foreach (var slot in activeSlots)
            {
                var key = CellKey(date, slot.Id);
                if (!occupiedKeys.Contains(key))
                    emptyCells.Add((date, slot));
            }
        }

        if (emptyCells.Count == 0)
            return new GeneratePlanResult(0, 0);

        // 5. Load candidate recipes (up to 50)
        var recipesReadModels = await recipeReader.SearchAsync(string.Empty, maxResults: 50, ct);
        var candidates = recipesReadModels
            .Select(r => new CandidateRecipe(r.RecipeId, r.Name, r.TagIds, r.DefaultServings, null))
            .ToList();

        // 6. Build PlannerMealSlotContext list
        var contexts = new List<PlannerMealSlotContext>();
        foreach (var (date, slot) in emptyCells)
        {
            var constraints = constraintResolver.ResolveForGeneration(slot.Id, slot, allPrefs);
            contexts.Add(new PlannerMealSlotContext(
                Date: date,
                MealSlotId: slot.Id,
                SlotLabel: slot.Label,
                EffectiveAttendees: constraints.EffectiveAttendees,
                Constraints: constraints,
                CandidateRecipes: candidates));
        }

        // 7. Invoke IMealPlanner (UNTRUSTED — output always goes through ACL)
        var rawProposals = await planner.ProposeWeekAsync(contexts, effectiveWeights, ct);

        // 8. Validate each proposal through ProposalAcl
        var validatedProposals = new List<ProposedMeal>();
        var unfilledCount = 0;

        // Build a lookup for O(1) context retrieval
        var contextLookup = contexts.ToDictionary(c => CellKey(c.Date, c.MealSlotId));

        foreach (var raw in rawProposals)
        {
            var cellKey = CellKey(raw.Date, raw.MealSlotId);
            if (!contextLookup.TryGetValue(cellKey, out var ctx))
            {
                // AI proposed a cell we didn't ask about — discard
                unfilledCount++;
                continue;
            }

            var result = ProposalAcl.Validate(raw, ctx.CandidateRecipes, ctx.Constraints);
            if (result.IsValid && result.ValidatedProposal is not null)
                validatedProposals.Add(result.ValidatedProposal);
            else
                unfilledCount++;
        }

        // Count cells that got no proposal at all
        var proposedCellKeys = rawProposals.Select(p => CellKey(p.Date, p.MealSlotId)).ToHashSet();
        unfilledCount += emptyCells.Count(c => !proposedCellKeys.Contains(CellKey(c.Date, c.Slot.Id)));

        // 9. Write validated proposals to the pending store
        await proposalStore.SetAsync(storeKey, validatedProposals, ct);

        return new GeneratePlanResult(
            ProposedCount: validatedProposals.Count,
            UnfilledCount: unfilledCount);
    }

    private static string CellKey(DateOnly date, MealSlotId slotId) =>
        $"{date:yyyy-MM-dd}_{slotId.Value:N}";
}

/// <summary>Result of <see cref="GeneratePlanService.ExecuteAsync"/>.</summary>
public sealed record GeneratePlanResult(
    /// <summary>Number of cells that received a validated AI proposal.</summary>
    int ProposedCount,
    /// <summary>Number of cells that could not be filled (no proposal or ACL rejection).</summary>
    int UnfilledCount);
