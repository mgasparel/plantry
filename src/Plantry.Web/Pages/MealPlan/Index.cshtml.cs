using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using DomainMealPlan = Plantry.MealPlanning.Domain.MealPlan;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Plantry.Web.Pages.MealPlan;

[Authorize]
public sealed class IndexModel(
    IMealPlanRepository mealPlanRepo,
    IMealSlotConfigRepository slotConfigRepo,
    AssignMealService assignService,
    MoveMealService moveService,
    ShopForWeekService shopForWeekService,
    GeneratePlanService generatePlanService,
    AcceptProposalService acceptProposalService,
    IPendingProposalStore pendingProposalStore,
    PlanFulfillmentService fulfillmentService,
    PlanCostingService costingService,
    PlanInsightsService planInsightsService,
    IRecipeReadModel recipeReader,
    IMealPlanCatalogProductReader catalogReader,
    IHouseholdMemberReader memberReader,
    ITenantContext tenant,
    UserManager<AppUser> userManager,
    IClock clock) : PageModel
{
    public DateOnly WeekStart { get; private set; }
    public DateOnly PrevWeekStart { get; private set; }
    public DateOnly NextWeekStart { get; private set; }
    public DateOnly ThisWeekStart { get; private set; }
    public string WeekLabel { get; private set; } = "";
    public bool HasSlots { get; private set; }

    /// <summary>True when there are empty cells in the current week (enables the Auto-fill button).</summary>
    public bool HasEmptyCells { get; private set; }

    public List<DayColumn> WeekDays { get; private set; } = [];
    public List<SlotRow> Slots { get; private set; } = [];
    public List<HouseholdMember> Members { get; private set; } = [];

    /// <summary>All planned meals for this week, keyed by "date_slotId".</summary>
    public Dictionary<string, List<MealCellVm>> MealsByCell { get; private set; } = [];

    /// <summary>Pending AI proposals for this week, keyed by "date_slotId".</summary>
    public Dictionary<string, ProposedMeal> PendingProposals { get; private set; } = [];

    /// <summary>Number of pending AI proposals awaiting user action.</summary>
    public int PendingCount => PendingProposals.Count;

    /// <summary>Advisory insight callouts for the rail, derived from the loaded week (presentation only).</summary>
    public List<InsightCallout> Insights { get; private set; } = [];

    /// <summary>Rolled-up week cost for the budget chip. Null when no pricing data available.</summary>
    public decimal? WeekTotalCost { get; private set; }

    /// <summary>True when any week cost was computed with partial pricing data.</summary>
    public bool WeekCostIsPartial { get; private set; }

    /// <summary>Builds the store key for the pending proposal store: {householdId}_{weekStart:yyyyMMdd}_{sessionId}.</summary>
    private string BuildStoreKey(HouseholdId householdId) =>
        $"{householdId.Value:N}_{WeekStart:yyyyMMdd}_{HttpContext.Session.Id}";

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync(string? week = null, CancellationToken ct = default)
    {
        await LoadWeekAsync(week, ct);
        return Page();
    }

    // htmx fragment — returns the week grid partial only
    public async Task<IActionResult> OnGetGridAsync(string? week = null, CancellationToken ct = default)
    {
        await LoadWeekAsync(week, ct);
        return Partial("_WeekGrid", this);
    }

    // ── Editor fragment — GET ─────────────────────────────────────────────────

    public async Task<IActionResult> OnGetEditorAsync(
        string date, Guid slotId, Guid? mealId = null, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var slot = await GetSlotAsync(householdId, MealSlotId.From(slotId), ct);
        if (slot is null) return NotFound();

        Members = (await memberReader.ListMembersAsync(ct)).ToList();

        // If editing an existing meal, load it
        MealEditorVm? vm = null;
        if (mealId.HasValue)
        {
            var plan = await mealPlanRepo.FindByWeekAsync(
                householdId, DomainMealPlan.NormalizeToMonday(parsedDate), ct);

            var meal = plan?.PlannedMeals.FirstOrDefault(m => m.Id.Value == mealId.Value);
            if (meal is not null)
            {
                // Resolve dish names + live fulfillment/cost so an existing meal opens in the editor
                // with the same per-dish "% in pantry · $cost" the prototype shows.
                var today = DateOnly.FromDateTime(DateTime.Today);
                var dishes = new List<EditorDishVm>();
                foreach (var d in meal.PlannedDishes.OrderBy(d => d.Ordinal))
                {
                    if (d.RecipeId.HasValue)
                    {
                        var r = await recipeReader.GetByIdAsync(d.RecipeId.Value, ct);
                        var enr = await recipeReader.GetEnrichmentAsync(d.RecipeId.Value, d.Servings, today, ct);
                        decimal? costPerServing = enr?.TotalCost is { } total && d.Servings > 0
                            ? total / d.Servings
                            : null;
                        dishes.Add(new EditorDishVm(
                            DishKind.Recipe, d.RecipeId.Value, r?.Name ?? "Unknown recipe", d.Servings, d.Ordinal,
                            enr?.FulfillmentPercent, costPerServing, r?.HasPhoto ?? false));
                    }
                    else if (d.ProductId.HasValue)
                    {
                        var names = await catalogReader.ResolveNamesAsync([d.ProductId.Value], ct);
                        var name = names.GetValueOrDefault(d.ProductId.Value, "Unknown product");
                        dishes.Add(new EditorDishVm(DishKind.Product, d.ProductId.Value, name, d.Servings, d.Ordinal));
                    }
                }

                vm = new MealEditorVm(
                    meal.Id.Value,
                    parsedDate,
                    slot.Label,
                    slot.DefaultAttendees,
                    meal.AttendeesOverride,
                    meal.Note,
                    dishes,
                    IsEditing: true);
            }
        }

        vm ??= new MealEditorVm(
            null, parsedDate, slot.Label, slot.DefaultAttendees,
            null, null, [], IsEditing: false);

        return Partial("_MealEditor", new EditorPageModel(vm, Members, slot));
    }

    // ── Assign meal POST ─────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostAssignAsync(
        string date, Guid slotId,
        [FromForm] string mode,
        [FromForm] string? note,
        [FromForm] List<string>? dishKinds,
        [FromForm] List<Guid>? dishItemIds,
        [FromForm] List<int>? dishServings,
        [FromForm] List<Guid>? attendeesOverride,
        [FromForm] bool attendeesOverridden = false,
        [FromForm] Guid? mealId = null,
        CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var userId = await GetCurrentUserIdAsync(ct);
        var sid = MealSlotId.From(slotId);
        var mid = mealId.HasValue ? PlannedMealId.From(mealId.Value) : (PlannedMealId?)null;

        List<Guid>? overrideList = attendeesOverridden ? attendeesOverride ?? [] : null;

        string? hardStanceWarning = null;
        if (mode == "note")
        {
            if (string.IsNullOrWhiteSpace(note)) return BadRequest("Note is required.");
            var noteResult = await assignService.AssignNoteAsync(householdId, parsedDate, sid, note!, overrideList, userId, mid, ct);
            hardStanceWarning = noteResult.HardStanceWarning;
        }
        else
        {
            // Build dishes from three index-aligned arrays (kind, itemId, servings) so that
            // servings are never mis-mapped when a meal mixes recipe and product dishes.
            var specs = BuildDishSpecs(dishKinds, dishItemIds, dishServings);
            if (specs.Count == 0) return BadRequest("At least one dish is required.");
            var dishResult = await assignService.AssignDishesAsync(householdId, parsedDate, sid, specs, overrideList, userId, mid, ct);
            hardStanceWarning = dishResult.HardStanceWarning;
        }

        // Return the updated cell fragment, including any dietary warning so the UI can display it
        return await CellFragmentAsync(householdId, parsedDate, sid, hardStanceWarning, ct);
    }

    // ── Clear meal POST ──────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostClearAsync(
        string date, Guid slotId, Guid mealId, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(slotId);
        await assignService.ClearMealAsync(householdId, parsedDate, PlannedMealId.From(mealId), ct);

        return await CellFragmentAsync(householdId, parsedDate, sid, ct);
    }

    // ── Move meal POST ───────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostMoveAsync(
        Guid mealId,
        string toDate, Guid toSlotId,
        CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(toDate, out var to))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        await moveService.MoveAsync(householdId, PlannedMealId.From(mealId), to, MealSlotId.From(toSlotId), ct);

        // Return the whole grid for the destination week
        var weekStr = DomainMealPlan.NormalizeToMonday(to).ToString("yyyy-MM-dd");
        await LoadWeekAsync(weekStr, ct);
        return Partial("_WeekGrid", this);
    }

    // ── AI generate plan POST ────────────────────────────────────────────────

    public async Task<IActionResult> OnPostGenerateAsync(string? week = null, CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var weekStart = week is not null && DateOnly.TryParse(week, out var parsed)
            ? DomainMealPlan.NormalizeToMonday(parsed)
            : DomainMealPlan.NormalizeToMonday(DateOnly.FromDateTime(DateTime.Today));

        // Ensure session is started so Session.Id is stable
        await HttpContext.Session.LoadAsync(ct);

        // Rebuild WeekStart so BuildStoreKey is correct
        await LoadWeekAsync(week, ct);

        var storeKey = BuildStoreKey(householdId);
        await generatePlanService.ExecuteAsync(householdId, weekStart, storeKey, null, ct);

        // Reload to pick up pending proposals
        await LoadWeekAsync(week, ct);
        return Partial("_WeekGrid", this);
    }

    // ── Accept all proposals POST ────────────────────────────────────────────

    public async Task<IActionResult> OnPostAcceptAllAsync(string? week = null, CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        await HttpContext.Session.LoadAsync(ct);
        await LoadWeekAsync(week, ct);

        var storeKey = BuildStoreKey(householdId);
        var userId = await GetCurrentUserIdAsync(ct);
        await acceptProposalService.AcceptAllAsync(householdId, WeekStart, storeKey, userId, ct);

        await LoadWeekAsync(week, ct);
        return Partial("_WeekGrid", this);
    }

    // ── Discard all proposals POST ───────────────────────────────────────────

    public async Task<IActionResult> OnPostDiscardAsync(string? week = null, CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        await HttpContext.Session.LoadAsync(ct);
        await LoadWeekAsync(week, ct);

        var storeKey = BuildStoreKey(householdId);
        await acceptProposalService.DiscardAsync(storeKey, ct);

        await LoadWeekAsync(week, ct);
        return Partial("_WeekGrid", this);
    }

    // ── Accept single cell POST ──────────────────────────────────────────────

    public async Task<IActionResult> OnPostAcceptCellAsync(
        string date, Guid slotId, string? week = null, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(slotId);
        await HttpContext.Session.LoadAsync(ct);

        var storeKey = BuildStoreKey(householdId);
        var userId = await GetCurrentUserIdAsync(ct);
        await acceptProposalService.AcceptCellAsync(householdId, parsedDate, sid, storeKey, userId, ct);

        // Return the full week grid so the pending bar count is always fresh (pending bar lives
        // inside _WeekGrid, so a cell-only swap would leave it stale after per-cell operations).
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);
        return Partial("_WeekGrid", this);
    }

    // ── Reject single cell POST ──────────────────────────────────────────────

    public async Task<IActionResult> OnPostRejectCellAsync(
        string date, Guid slotId, string? week = null, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(slotId);
        await HttpContext.Session.LoadAsync(ct);

        var storeKey = BuildStoreKey(householdId);
        await acceptProposalService.RejectCellAsync(storeKey, parsedDate, sid, ct);

        // Return the full week grid so the pending bar count is always fresh.
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);
        return Partial("_WeekGrid", this);
    }

    // ── Shop for this week POST ──────────────────────────────────────────────

    public async Task<IActionResult> OnPostShopAsync(CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekStart = DomainMealPlan.NormalizeToMonday(today);

        // Allow the week query param to be forwarded so "shop for another week" works too.
        // The week comes from the form's hidden field emitted by the grid.
        if (Request.Form.TryGetValue("week", out var weekStr) &&
            DateOnly.TryParse(weekStr, out var parsed))
        {
            weekStart = DomainMealPlan.NormalizeToMonday(parsed);
        }

        var result = await shopForWeekService.ExecuteAsync(householdId, weekStart, ct);
        return new JsonResult(new { itemsAdded = result.ItemsAdded });
    }

    // ── Search fragments ─────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetSearchAsync(string q, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var recipes = await recipeReader.SearchAsync(q, 6, ct);

        // Enrich each recipe hit with live fulfillment + per-serving cost so the picker can show
        // "{pct}% in pantry · $cost" exactly like the prototype (PL.recipeStock). MealPlanning
        // borrows Recipes' computations — it never recomputes them (domain-model §1).
        var hits = new List<RecipeHitVm>(recipes.Count);
        foreach (var r in recipes)
        {
            var enr = await recipeReader.GetEnrichmentAsync(r.RecipeId, r.DefaultServings, today, ct);
            decimal? costPerServing = enr?.TotalCost is { } total && r.DefaultServings > 0
                ? total / r.DefaultServings
                : null;
            hits.Add(new RecipeHitVm(
                r.RecipeId, r.Name, r.DefaultServings,
                enr?.FulfillmentPercent, costPerServing, r.HasPhoto));
        }

        var products = await catalogReader.SearchAsync(q, 5, ct);
        return Partial("_DishSearch", new DishSearchVm(q, hits, products));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task LoadWeekAsync(string? weekParam, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        ThisWeekStart = DomainMealPlan.NormalizeToMonday(today);

        WeekStart = weekParam is not null && DateOnly.TryParse(weekParam, out var parsed)
            ? DomainMealPlan.NormalizeToMonday(parsed)
            : ThisWeekStart;

        PrevWeekStart = WeekStart.AddDays(-7);
        NextWeekStart = WeekStart.AddDays(7);

        var end = WeekStart.AddDays(6);
        WeekLabel = WeekStart.Month == end.Month
            ? $"{WeekStart:MMM d} – {end.Day}, {WeekStart:yyyy}"
            : $"{WeekStart:MMM d} – {end:MMM d}, {WeekStart:yyyy}";

        WeekDays = Enumerable.Range(0, 7)
            .Select(i =>
            {
                var d = WeekStart.AddDays(i);
                return new DayColumn(d.ToString("ddd"), d.Day.ToString(), d.ToString("yyyy-MM-dd"), d == today);
            })
            .ToList();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var config = await slotConfigRepo.FindByHouseholdAsync(householdId, ct);

        if (config is not null)
        {
            Slots = config.Slots
                .Where(s => s.IsActive)
                .OrderBy(s => s.Ordinal)
                .Select(s => new SlotRow(s.Id, s.Label, s.DefaultAttendees))
                .ToList();
        }

        HasSlots = Slots.Count > 0;

        // Load planned meals for this week, enriched with live fulfillment and cost.
        DomainMealPlan? loadedPlan = null;
        if (HasSlots)
        {
            var plan = await mealPlanRepo.FindByWeekAsync(householdId, WeekStart, ct);
            loadedPlan = plan;

            if (plan is not null)
            {
                // Compute week-level cost roll-up for the budget chip.
                var weekCost = await costingService.RollUpWeekAsync(plan, ct);
                WeekTotalCost = weekCost.Amount;
                WeekCostIsPartial = weekCost.Completeness == CostCompleteness.Partial;

                foreach (var meal in plan.PlannedMeals.OrderBy(m => m.Ordinal))
                {
                    var key = CellKey(meal.Date, meal.MealSlotId);
                    if (!MealsByCell.TryGetValue(key, out var list))
                    {
                        list = [];
                        MealsByCell[key] = list;
                    }

                    // Resolve names for dishes
                    var dishNames = new List<string>();
                    foreach (var d in meal.PlannedDishes.OrderBy(x => x.Ordinal))
                    {
                        if (d.RecipeId.HasValue)
                        {
                            var r = await recipeReader.GetByIdAsync(d.RecipeId.Value, ct);
                            dishNames.Add(r?.Name ?? "Unknown recipe");
                        }
                        else if (d.ProductId.HasValue)
                        {
                            var names = await catalogReader.ResolveNamesAsync([d.ProductId.Value], ct);
                            dishNames.Add(names.GetValueOrDefault(d.ProductId.Value, "Unknown product"));
                        }
                    }

                    // Compute per-meal fulfillment and cost enrichment.
                    MealFulfillmentVm? enrichment = null;
                    if (meal.Note is null && meal.PlannedDishes.Count > 0)
                    {
                        var fulfillment = await fulfillmentService.RollUpMealAsync(meal, today, ct);
                        var mealCost = await costingService.RollUpMealAsync(meal, ct);
                        enrichment = new MealFulfillmentVm(
                            fulfillment.FulfillmentPercent,
                            fulfillment.HasExpiringIngredients,
                            mealCost.Amount,
                            mealCost.Completeness == CostCompleteness.Partial);
                    }

                    list.Add(new MealCellVm(meal.Id.Value, meal.Note, dishNames, meal.AttendeesOverride ?? [], enrichment));
                }
            }
        }

        Members = (await memberReader.ListMembersAsync(ct)).ToList();

        // Compute HasEmptyCells (used to enable/disable Auto-fill button) and the open-cell count.
        int emptyCells = 0;
        if (HasSlots)
        {
            foreach (var d in WeekDays)
            {
                foreach (var s in Slots)
                {
                    var key = CellKey(DateOnly.Parse(d.DateIso), s.Id);
                    if (!MealsByCell.ContainsKey(key)) emptyCells++;
                }
            }
            HasEmptyCells = emptyCells > 0;
        }

        // Load pending AI proposals for this week from the store
        await LoadPendingProposalsAsync(householdId, ct);

        await BuildInsightsAsync(loadedPlan, emptyCells, ct);
    }

    /// <summary>
    /// Builds advisory rail callouts from the already-loaded week using
    /// <see cref="PlanInsightsService"/> (P3-5).
    /// </summary>
    private async Task BuildInsightsAsync(
        DomainMealPlan? plan,
        int emptyCells,
        CancellationToken ct)
    {
        Insights = [];
        if (!HasSlots) return;

        // Build the full cell key list (date × slot) for the unfilled-slot rule.
        var allCells = new List<string>();
        foreach (var d in WeekDays)
        {
            foreach (var s in Slots)
                allCells.Add(CellKey(DateOnly.Parse(d.DateIso), s.Id));
        }

        // When the plan aggregate hasn't been created yet (new week, no meals), use an
        // empty plan so the unfilled-slot and expiring-stock rules still fire correctly.
        var effectivePlan = plan ?? DomainMealPlan.Start(
            HouseholdId.From(tenant.HouseholdId ?? Guid.Empty),
            WeekStart,
            clock);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var insights = await planInsightsService.InspectAsync(
            effectivePlan,
            allCells,
            weekTotalCost: WeekTotalCost,
            budgetTarget: null, // no persisted budget target yet — suppresses over-budget rule
            priorPlans: null,   // prior plan history not loaded — suppresses vs-history repetition rule
            today,
            ct);

        Insights = insights.Insights
            .Select(i => new InsightCallout(i.Tone, i.Icon, i.Title, i.Body, i.ActionUrl))
            .ToList();
    }

    private async Task LoadPendingProposalsAsync(HouseholdId householdId, CancellationToken ct)
    {
        try
        {
            // Only load pending proposals if a session exists (session may not be started yet on GET)
            await HttpContext.Session.LoadAsync(ct);
            var storeKey = BuildStoreKey(householdId);
            var pending = await pendingProposalStore.GetAsync(storeKey, ct);
            PendingProposals = pending.ToDictionary(
                p => CellKey(p.Date, p.MealSlotId),
                p => p);
        }
        catch
        {
            // Session may not be available in all contexts (e.g. non-session requests); degrade gracefully
            PendingProposals = [];
        }
    }

    private async Task<IActionResult> CellFragmentAsync(HouseholdId householdId, DateOnly date, MealSlotId slotId, CancellationToken ct)
        => await CellFragmentAsync(householdId, date, slotId, null, ct);

    private async Task<IActionResult> CellFragmentAsync(HouseholdId householdId, DateOnly date, MealSlotId slotId, string? hardStanceWarning, CancellationToken ct)
    {
        await LoadWeekAsync(DomainMealPlan.NormalizeToMonday(date).ToString("yyyy-MM-dd"), ct);
        var key = CellKey(date, slotId);
        var meals = MealsByCell.GetValueOrDefault(key) ?? [];
        var slot = Slots.FirstOrDefault(s => s.Id == slotId);
        var pending = PendingProposals.GetValueOrDefault(key);

        // Resolve ghost cell recipe names for the cell fragment (fix: was using literal "Recipe").
        // _WeekGrid resolves these inline via ResolveRecipeName; the cell fragment must do the same.
        IReadOnlyList<string>? ghostDishNames = null;
        if (pending is not null)
        {
            var names = new List<string>(pending.Dishes.Count);
            foreach (var d in pending.Dishes.OrderBy(x => x.Ordinal))
            {
                var r = await recipeReader.GetByIdAsync(d.RecipeId, ct);
                names.Add(r?.Name ?? "Unknown recipe");
            }
            ghostDishNames = names;
        }

        // Return the cell fragment plus an out-of-band rail refresh. LoadWeekAsync (called above)
        // has already recomputed Model.Insights for the mutated plan, so the rail here is fresh.
        // Routing every cell-targeted mutation through this single helper is what guarantees the
        // ticket's "recompute on EVERY change" — no per-handler wiring to forget.
        var cellVm = new CellFragmentVm(date, slotId, slot?.Label ?? "", meals, WeekStart, Members, hardStanceWarning, pending, ghostDishNames);
        var railVm = new PlanRailVm(Insights, PendingCount, Oob: true);
        return Partial("_CellWithRail", new CellWithRailVm(cellVm, railVm));
    }

    private async Task<MealSlot?> GetSlotAsync(HouseholdId householdId, MealSlotId slotId, CancellationToken ct)
    {
        var config = await slotConfigRepo.FindByHouseholdAsync(householdId, ct);
        return config?.Slots.FirstOrDefault(s => s.Id == slotId && s.IsActive);
    }

    private async Task<Guid> GetCurrentUserIdAsync(CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(User);
        return user is not null ? Guid.Parse(user.Id) : Guid.Empty;
    }

    /// <summary>
    /// Builds DishSpec list from three index-aligned form arrays (kinds, itemIds, servings).
    /// The three arrays are emitted in display order by the editor, so index i of each array
    /// always refers to the same dish — servings can never be mis-mapped across kind groups.
    /// </summary>
    private static List<DishSpec> BuildDishSpecs(
        List<string>? kinds, List<Guid>? itemIds, List<int>? servings)
    {
        if (kinds is null || itemIds is null) return [];
        var count = Math.Min(kinds.Count, itemIds.Count);
        var specs = new List<DishSpec>(count);
        for (int i = 0; i < count; i++)
        {
            var kind = kinds[i].Equals("recipe", StringComparison.OrdinalIgnoreCase)
                ? DishKind.Recipe
                : DishKind.Product;
            var sv = servings != null && i < servings.Count ? servings[i] : 1;
            specs.Add(new DishSpec(kind, itemIds[i], Math.Max(1, sv)));
        }
        return specs;
    }

    public static string CellKey(DateOnly date, MealSlotId slotId) => $"{date:yyyy-MM-dd}_{slotId.Value:N}";

    /// <summary>
    /// Used by _WeekGrid.cshtml to resolve a recipe name for ghost cell display.
    /// Delegates to the IndexModel's recipeReader (held as a field via DI).
    /// </summary>
    public async Task<string> ResolveRecipeNameAsync(Guid recipeId)
    {
        var r = await recipeReader.GetByIdAsync(recipeId);
        return r?.Name ?? "Unknown recipe";
    }

    /// <summary>Static shim for Razor partial call syntax used in _WeekGrid.cshtml.</summary>
    public static Task<string> ResolveRecipeName(Guid recipeId, IndexModel model) =>
        model.ResolveRecipeNameAsync(recipeId);

    // ── View models ───────────────────────────────────────────────────────────

    public sealed record DayColumn(string DayName, string DateLabel, string DateIso, bool IsToday);
    public sealed record SlotRow(MealSlotId Id, string Label, List<Guid> DefaultAttendees);

    /// <summary>
    /// An advisory insight rendered in the planner rail. Tone: warn|info|good. Icon: sprite suffix.
    /// ActionUrl: optional href for the callout's action link (e.g. "/Recipes?filter=use-soon").
    /// </summary>
    public sealed record InsightCallout(string Tone, string Icon, string Title, string Body, string? ActionUrl = null);

    /// <summary>Per-meal fulfillment/cost enrichment for the grid cell (P3-4).</summary>
    public sealed record MealFulfillmentVm(
        int FulfillmentPercent,
        bool HasExpiringIngredients,
        decimal? TotalCost,
        bool CostIsPartial);

    public sealed record MealCellVm(
        Guid MealId,
        string? Note,
        List<string> DishNames,
        List<Guid> EffectiveAttendees,
        MealFulfillmentVm? Enrichment = null);

    public sealed record MealEditorVm(
        Guid? MealId,
        DateOnly Date,
        string SlotLabel,
        List<Guid> SlotDefaultAttendees,
        List<Guid>? AttendeesOverride,
        string? Note,
        List<EditorDishVm> Dishes,
        bool IsEditing);

    public sealed record EditorDishVm(
        DishKind Kind, Guid ItemId, string Name, int Servings, int Ordinal,
        int? FulfillmentPercent = null, decimal? CostPerServing = null, bool HasPhoto = false);

    /// <summary>A recipe row in the dish-search dropdown, enriched with live fulfillment/cost + photo flag.</summary>
    public sealed record RecipeHitVm(
        Guid RecipeId, string Name, int DefaultServings,
        int? FulfillmentPercent, decimal? CostPerServing, bool HasPhoto);

    public sealed record EditorPageModel(MealEditorVm Vm, List<HouseholdMember> Members, MealSlot Slot);
    public sealed record CellFragmentVm(DateOnly Date, MealSlotId SlotId, string SlotLabel, List<MealCellVm> Meals, DateOnly WeekStart, List<HouseholdMember> Members, string? HardStanceWarning = null, ProposedMeal? PendingProposal = null, IReadOnlyList<string>? GhostDishNames = null);

    /// <summary>
    /// View model for the advisory insights rail (P3-5). Rendered inline inside <c>_WeekGrid</c>
    /// on a full grid swap, and out-of-band (<paramref name="Oob"/> = true) alongside a cell
    /// fragment so the rail recomputes on EVERY plan change — see <c>_PlanRail.cshtml</c>.
    /// </summary>
    public sealed record PlanRailVm(IReadOnlyList<InsightCallout> Insights, int PendingCount, bool Oob = false);

    /// <summary>
    /// Combines a single cell fragment with an out-of-band rail refresh. Every cell-targeted
    /// plan mutation returns through this (via <c>CellFragmentAsync</c>) so the "recompute the
    /// rail on every change" invariant lives in exactly one place rather than per-handler.
    /// </summary>
    public sealed record CellWithRailVm(CellFragmentVm Cell, PlanRailVm Rail);
    public sealed record DishSearchVm(string Query, IReadOnlyList<RecipeHitVm> Recipes, IReadOnlyList<MealPlanProductReadModel> Products);
    public sealed record MealCardVm(MealCellVm Meal, string DateIso, MealSlotId SlotId, List<HouseholdMember> Members);
    public sealed record GhostCellVm(DateOnly Date, MealSlotId SlotId, string SlotLabel, ProposedMeal Proposal, IReadOnlyList<string> DishNames);
}
