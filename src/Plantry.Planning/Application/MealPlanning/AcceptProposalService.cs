using Microsoft.Extensions.Logging;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Planning.Application;

/// <summary>
/// Application service for accepting or discarding AI-generated meal proposals (P3-6a).
/// Re-validates proposals at the trust boundary before committing — the store could have
/// been populated under stale data (race: recipe deleted between generate and accept).
/// </summary>
public sealed class AcceptProposalService(
    IMealPlanRepository mealPlanRepo,
    IMealSlotConfigRepository slotConfigRepo,
    IUserPreferenceRepository prefsRepo,
    IRecipeReadModel recipeReader,
    IPendingProposalStore proposalStore,
    MealConstraintResolver constraintResolver,
    IClock clock,
    ILogger<AcceptProposalService> logger)
{
    public const string RecipeNoLongerPlatedReason =
        "This suggestion includes a recipe that is no longer plated. Generate a new suggestion or add the recipe manually.";

    /// <summary>
    /// Accepts all pending proposals for a week. Re-validates each proposal (trust boundary).
    /// Calls MealPlan.ApplyProposal atomically. Clears the store on completion.
    /// </summary>
    public async Task<AcceptResult> AcceptAllAsync(
        HouseholdId householdId,
        DateOnly weekStart,
        string storeKey,
        Guid userId,
        CancellationToken ct = default)
    {
        var pending = await proposalStore.GetAsync(storeKey, ct);
        if (pending.Count == 0)
            return new AcceptResult(0, 0);

        var monday = Domain.MealPlan.NormalizeToMonday(weekStart);
        var context = await BuildValidationContextAsync(householdId, pending, ct);

        var validatedProposals = new List<ProposedMeal>();
        var rejected = 0;
        var rejections = new List<ProposalRejection>();

        foreach (var proposal in pending)
        {
            var cellKey = CellKey(proposal.Date, proposal.MealSlotId);
            var constraints = context.ConstraintMap.GetValueOrDefault(cellKey) ?? GenerationConstraints.Empty;
            var result = context.UnplatedCellKeys.Contains(cellKey)
                ? AclValidationResult.Unfilled
                : ProposalAcl.Validate(proposal, context.Candidates, constraints);

            if (result.IsValid && result.ValidatedProposal is not null)
                validatedProposals.Add(result.ValidatedProposal);
            else
            {
                logger.LogWarning(
                    "Proposal re-validation failed for cell {Date}/{SlotId} — recipe may have been removed.",
                    proposal.Date, proposal.MealSlotId.Value);
                rejected++;
                rejections.Add(new ProposalRejection(
                    proposal.Date,
                    proposal.MealSlotId,
                    context.SlotLabels.GetValueOrDefault(cellKey, proposal.MealSlotId.Value.ToString()),
                    context.UnplatedCellKeys.Contains(cellKey)
                        ? RecipeNoLongerPlatedReason
                        : "This suggestion could not be accepted because one or more recipes are no longer available."));
            }
        }

        int accepted = 0;
        if (validatedProposals.Count > 0)
        {
            var plan = await mealPlanRepo.FindOrCreateAsync(householdId, monday, clock, ct);
            accepted = plan.ApplyProposal(validatedProposals, userId, clock);
            await mealPlanRepo.SaveChangesAsync(ct);
        }

        await proposalStore.ClearAsync(storeKey, ct);
        logger.LogInformation(
            "AcceptAll completed for week {WeekStart}. Accepted: {Accepted}, Rejected: {Rejected}.",
            weekStart, accepted, rejected);
        return new AcceptResult(accepted, rejected, rejections);
    }

    /// <summary>
    /// Accepts the proposal for a single cell. Re-validates at the trust boundary.
    /// Removes that entry from the store.
    /// </summary>
    public async Task<AcceptCellResult> AcceptCellAsync(
        HouseholdId householdId,
        DateOnly date,
        MealSlotId slotId,
        string storeKey,
        Guid userId,
        CancellationToken ct = default)
    {
        var pending = await proposalStore.GetAsync(storeKey, ct);
        var proposal = pending.FirstOrDefault(p => p.Date == date && p.MealSlotId == slotId);
        if (proposal is null)
            return new AcceptCellResult(Accepted: false, Reason: "No pending proposal for this cell.");

        var context = await BuildValidationContextAsync(householdId, [proposal], ct);
        var cellKey = CellKey(date, slotId);
        var constraints = context.ConstraintMap.GetValueOrDefault(cellKey) ?? GenerationConstraints.Empty;

        var isUnplated = context.UnplatedCellKeys.Contains(cellKey);
        var result = isUnplated
            ? AclValidationResult.Unfilled
            : ProposalAcl.Validate(proposal, context.Candidates, constraints);
        if (!result.IsValid || result.ValidatedProposal is null)
        {
            logger.LogWarning(
                "AcceptCell re-validation failed for cell {Date}/{SlotId} — recipe may have been removed.",
                date, slotId.Value);
            await proposalStore.RemoveAsync(storeKey, date, slotId, ct);
            var reason = isUnplated
                ? RecipeNoLongerPlatedReason
                : "This suggestion could not be accepted because one or more recipes are no longer available.";
            return new AcceptCellResult(
                Accepted: false,
                Reason: reason,
                Rejection: new ProposalRejection(
                    date,
                    slotId,
                    context.SlotLabels.GetValueOrDefault(cellKey, slotId.Value.ToString()),
                    reason));
        }

        var monday = Domain.MealPlan.NormalizeToMonday(date);
        var plan = await mealPlanRepo.FindOrCreateAsync(householdId, monday, clock, ct);
        plan.ApplyProposal([result.ValidatedProposal], userId, clock);
        await mealPlanRepo.SaveChangesAsync(ct);

        await proposalStore.RemoveAsync(storeKey, date, slotId, ct);
        logger.LogInformation(
            "Proposal accepted for cell {Date}/{SlotId}.", date, slotId.Value);
        return new AcceptCellResult(Accepted: true, Reason: null);
    }

    /// <summary>Removes a pending proposal for a specific cell without affecting the plan.</summary>
    public async Task RejectCellAsync(
        string storeKey,
        DateOnly date,
        MealSlotId slotId,
        CancellationToken ct = default)
    {
        await proposalStore.RemoveAsync(storeKey, date, slotId, ct);
    }

    /// <summary>Discards all pending proposals. No plan changes.</summary>
    public async Task DiscardAsync(string storeKey, CancellationToken ct = default)
    {
        await proposalStore.ClearAsync(storeKey, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the validation context for a set of proposals: fresh candidate list + constraint map
    /// re-resolved from current household data (trust boundary — data may have changed since generate).
    /// </summary>
    private async Task<ProposalValidationContext>
        BuildValidationContextAsync(
            HouseholdId householdId,
            IReadOnlyList<ProposedMeal> proposals,
            CancellationToken ct)
    {
        // Fresh candidates: read the exact recipe ids in the pending proposals. The old 50-result search
        // could reject a valid suggestion simply because its name sorted after the inline-search page.
        var recipeIds = proposals
            .SelectMany(p => p.Dishes)
            .Select(d => d.RecipeId)
            .Distinct()
            .ToList();
        var recipesReadModels = await recipeReader.GetByIdsAsync(recipeIds, ct);
        var candidates = recipesReadModels
            .Values
            .Select(r => new CandidateRecipe(r.RecipeId, r.Name, r.TagIds, r.DefaultServings, null, IsPlated: r.IsPlated))
            .ToList();
        var unplatedCellKeys = proposals
            .Where(p => p.Dishes.Any(d => recipesReadModels.TryGetValue(d.RecipeId, out var recipe) && !recipe.IsPlated))
            .Select(p => CellKey(p.Date, p.MealSlotId))
            .ToHashSet();

        // Re-resolve constraints per slot
        var slotConfig = await slotConfigRepo.FindByHouseholdAsync(householdId, ct);
        var allPrefs = new List<UserPreference>();
        if (slotConfig is not null)
        {
            var allUserIds = slotConfig.Slots
                .Where(s => s.IsActive)
                .SelectMany(s => s.DefaultAttendees)
                .Distinct()
                .ToList();
            foreach (var uid in allUserIds)
            {
                var pref = await prefsRepo.FindByUserIdAsync(uid, ct);
                if (pref is not null) allPrefs.Add(pref);
            }
        }

        var constraintMap = new Dictionary<string, GenerationConstraints>();
        var slotLabels = new Dictionary<string, string>();
        foreach (var proposal in proposals)
        {
            var cellKey = CellKey(proposal.Date, proposal.MealSlotId);
            if (constraintMap.ContainsKey(cellKey)) continue;

            var slot = slotConfig?.Slots.FirstOrDefault(s => s.Id == proposal.MealSlotId && s.IsActive);
            constraintMap[cellKey] = slot is not null
                ? constraintResolver.ResolveForGeneration(slot.Id, slot, allPrefs)
                : GenerationConstraints.Empty;
            slotLabels[cellKey] = slot?.Label ?? proposal.MealSlotId.Value.ToString();
        }

        return new ProposalValidationContext(candidates, constraintMap, slotLabels, unplatedCellKeys);
    }

    private static string CellKey(DateOnly date, MealSlotId slotId) =>
        $"{date:yyyy-MM-dd}_{slotId.Value:N}";
}

/// <summary>Result of <see cref="AcceptProposalService.AcceptAllAsync"/>.</summary>
public sealed record AcceptResult(
    int Accepted,
    int Rejected,
    IReadOnlyList<ProposalRejection>? Rejections = null);

/// <summary>Result of <see cref="AcceptProposalService.AcceptCellAsync"/>.</summary>
public sealed record AcceptCellResult(
    bool Accepted,
    string? Reason,
    ProposalRejection? Rejection = null);

/// <summary>Structured identity and reason for a proposal skipped at acceptance time.</summary>
public sealed record ProposalRejection(
    DateOnly Date,
    MealSlotId SlotId,
    string SlotLabel,
    string Reason);

internal sealed record ProposalValidationContext(
    List<CandidateRecipe> Candidates,
    Dictionary<string, GenerationConstraints> ConstraintMap,
    Dictionary<string, string> SlotLabels,
    IReadOnlySet<string> UnplatedCellKeys);
