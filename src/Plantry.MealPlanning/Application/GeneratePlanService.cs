using Microsoft.Extensions.Logging;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service that drives the AI generate-plan flow (P3-6a, J7).
/// Orchestrates: load week plan → find empty cells → resolve constraints →
/// load candidates → detect irreconcilable hard-stance conflicts (C6) →
/// detect unfulfillable cells (no recipes for a Required tag in the full corpus) →
/// call IMealPlanner (untrusted) → validate via ProposalAcl → stage in IPendingProposalStore.
/// The AI output is never persisted directly — it goes to the pending store for user review.
/// Irreconcilable cells (C6: no single candidate satisfies every attendee) are skipped from
/// the planner request, counted as unfilled, and recorded in GeneratePlanResult.Conflicts.
/// Unfulfillable cells (an attendee's Required tag has no recipes in the full corpus) are
/// similarly skipped from the planner request and recorded in GeneratePlanResult.UnfulfillableCells.
/// </summary>
public sealed class GeneratePlanService(
    IMealPlanner planner,
    IMealPlanRepository mealPlanRepo,
    IMealSlotConfigRepository slotConfigRepo,
    IUserPreferenceRepository prefsRepo,
    IRecipeReadModel recipeReader,
    IPendingProposalStore proposalStore,
    MealConstraintResolver constraintResolver,
    ITagReader tagReader,
    ILogger<GeneratePlanService> logger)
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
        {
            logger.LogWarning(
                "Meal plan generation skipped for household {HouseholdId}, week {WeekStart} — no slot configuration.",
                householdId.Value, monday);
            return new GeneratePlanResult(0, 0, [], []);
        }

        var activeSlots = slotConfig.Slots
            .Where(s => s.IsActive)
            .OrderBy(s => s.Ordinal)
            .ToList();

        if (activeSlots.Count == 0)
        {
            logger.LogWarning(
                "Meal plan generation skipped for household {HouseholdId}, week {WeekStart} — no active meal slots.",
                householdId.Value, monday);
            return new GeneratePlanResult(0, 0, [], []);
        }

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
            return new GeneratePlanResult(0, 0, [], []);

        // 5. Load candidate recipes (up to 50)
        var recipesReadModels = await recipeReader.SearchAsync(string.Empty, maxResults: 50, ct);
        var candidates = recipesReadModels
            .Select(r => new CandidateRecipe(r.RecipeId, r.Name, r.TagIds, r.DefaultServings, null))
            .ToList();

        // 6. Load tag vocabulary once for unfulfillable tag name resolution.
        // Flattened to a lookup for O(1) access by tag ID.
        var allTags = await tagReader.ListGroupedAsync(ct);
        var tagNameLookup = allTags
            .SelectMany(g => g.Tags)
            .ToDictionary(t => t.TagId, t => t.Name);

        // 7. Build PlannerMealSlotContext list, running feasibility pre-checks before the AI call.
        //    Order of checks matters:
        //      a) HardConflict (C6): ≥2 attendees each have recipes, but no SINGLE recipe satisfies all.
        //         Fix: find a compromise dish or adjust attendance.
        //      b) Unfulfillable: one attendee's Required tag has ZERO recipes in the FULL corpus.
        //         Fix: add a recipe matching the tag.
        //    A cell that passes both checks is dispatched to the AI planner.
        var contexts = new List<PlannerMealSlotContext>();
        var hardConflictCells = new List<HardConflictCell>();
        var unfulfillableCells = new List<UnfulfillableCell>();
        var unfilledCount = 0;

        // Cache for tag→corpus-feasibility: invariant across cells within one generate call.
        // Avoids re-querying the same tag on every cell (e.g. 21 cells × N required tags).
        var tagFeasibilityCache = new Dictionary<Guid, bool>();

        foreach (var (date, slot) in emptyCells)
        {
            var constraints = constraintResolver.ResolveForGeneration(slot.Id, slot, allPrefs);

            // C6: detect whether no single candidate can satisfy all attendees.
            var conflict = HardConflictDetector.Detect(constraints, candidates);
            if (conflict is not null)
            {
                // Irreconcilable — skip from planner request, count as unfilled, record conflict.
                hardConflictCells.Add(new HardConflictCell(date, slot.Id, conflict));
                unfilledCount++;
                continue;
            }

            // Unfulfillability: check whether any Required tag has ZERO recipes in the full corpus.
            // Only run when no HardConflict was detected (conflict is classified first per spec).
            // Uses a targeted per-tag corpus query — never the 50-cap candidate list.
            // Results are memoized per tag across cells (feasibility is invariant within one generate call).
            var unfulfillable = await UnfulfillabilityDetector.DetectAsync(
                constraints,
                async tagId =>
                {
                    if (tagFeasibilityCache.TryGetValue(tagId, out var cached)) return cached;
                    var has = await recipeReader.AnyRecipeWithTagAsync(tagId, ct);
                    tagFeasibilityCache[tagId] = has;
                    return has;
                },
                ct);

            if (unfulfillable is not null)
            {
                // Resolve tag display name for the UI; fall back to a generic message when missing.
                var tagName = tagNameLookup.GetValueOrDefault(unfulfillable.UnfulfillableTagId);
                unfulfillableCells.Add(new UnfulfillableCell(date, slot.Id, unfulfillable, tagName));
                unfilledCount++;
                continue;
            }

            contexts.Add(new PlannerMealSlotContext(
                Date: date,
                MealSlotId: slot.Id,
                SlotLabel: slot.Label,
                EffectiveAttendees: constraints.EffectiveAttendees,
                Constraints: constraints,
                CandidateRecipes: candidates));
        }

        // 8. Invoke IMealPlanner (UNTRUSTED — output always goes through ACL)
        var rawProposals = contexts.Count > 0
            ? await planner.ProposeWeekAsync(contexts, effectiveWeights, ct)
            : new List<ProposedMeal>();

        // 9. Validate each proposal through ProposalAcl
        var validatedProposals = new List<ProposedMeal>();

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
        unfilledCount += contexts.Count(c => !proposedCellKeys.Contains(CellKey(c.Date, c.MealSlotId)));

        // 10. Write validated proposals to the pending store
        await proposalStore.SetAsync(storeKey, validatedProposals, ct);

        logger.LogInformation(
            "Meal plan generated for household {HouseholdId}, week {WeekStart}. Proposed: {ProposedCount}, Unfilled: {UnfilledCount}, Conflicts: {ConflictCount}, Unfulfillable: {UnfulfillableCount}.",
            householdId.Value, monday, validatedProposals.Count, unfilledCount,
            hardConflictCells.Count, unfulfillableCells.Count);

        return new GeneratePlanResult(
            ProposedCount: validatedProposals.Count,
            UnfilledCount: unfilledCount,
            Conflicts: hardConflictCells,
            UnfulfillableCells: unfulfillableCells);
    }

    private static string CellKey(DateOnly date, MealSlotId slotId) =>
        $"{date:yyyy-MM-dd}_{slotId.Value:N}";
}

/// <summary>Result of <see cref="GeneratePlanService.ExecuteAsync"/>.</summary>
public sealed record GeneratePlanResult(
    /// <summary>Number of cells that received a validated AI proposal.</summary>
    int ProposedCount,
    /// <summary>Number of cells that could not be filled (no proposal, ACL rejection, hard conflict, or unfulfillable).</summary>
    int UnfilledCount,
    /// <summary>
    /// Cells that were detected as irreconcilable hard-stance conflicts (C6): no single candidate
    /// recipe satisfies every attendee. These are excluded from the planner request and counted in
    /// UnfilledCount. The conflict descriptor carries attendee IDs and the clashing tag IDs.
    /// Request-scoped: not persisted — only relevant during the generate/review flow.
    /// </summary>
    IReadOnlyList<HardConflictCell> Conflicts,
    /// <summary>
    /// Cells that were detected as unfulfillable: at least one attendee has a Required tag for
    /// which ZERO recipes exist in the full corpus. These are excluded from the planner request
    /// and counted in UnfilledCount. Distinct from HardConflict — it is a property of
    /// (attendee's Required tag × corpus), not of attendees conflicting against each other.
    /// Request-scoped: not persisted — only relevant during the generate/review flow.
    /// </summary>
    IReadOnlyList<UnfulfillableCell> UnfulfillableCells);

/// <summary>
/// A single cell flagged as an irreconcilable hard-stance conflict during generation (C6).
/// </summary>
public sealed record HardConflictCell(
    DateOnly Date,
    MealSlotId MealSlotId,
    HardStanceConflict Conflict);

/// <summary>
/// A single cell flagged as unfulfillable during generation: one attendee's Required tag has
/// zero satisfying recipes in the full corpus. The AI is not called for this cell.
/// </summary>
/// <param name="Date">The date of the unfillable meal slot cell.</param>
/// <param name="MealSlotId">The slot ID of the unfillable cell.</param>
/// <param name="Reason">The unfulfillable finding: which attendee and which Required tag.</param>
/// <param name="TagName">
/// Display name of the unfulfillable tag, resolved from the tag vocabulary at generation time.
/// Null when the tag is no longer in the active vocabulary (archived or unknown).
/// </param>
public sealed record UnfulfillableCell(
    DateOnly Date,
    MealSlotId MealSlotId,
    UnfulfillableResult Reason,
    string? TagName);
