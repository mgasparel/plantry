using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using DomainMealPlan = Plantry.MealPlanning.Domain.MealPlan;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Plantry.Web.MealPlanning;

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
    SetPlanningSettingsService setPlanningSettingsService,
    IHouseholdPlanningSettingsRepository planningSettingsRepo,
    IWeekPlanningOverrideRepository weekOverrideRepo,
    IMealPlanWeekReadModel weekReadModel,
    Plantry.Recipes.Domain.FulfillmentService recipesFulfillmentService,
    Plantry.Recipes.Domain.CostingService recipesCostingService,
    ITenantContext tenant,
    UserManager<AppUser> userManager,
    IClock clock,
    ILogger<IndexModel> logger) : PageModel
{
    public DateOnly WeekStart { get; private set; }
    public DateOnly PrevWeekStart { get; private set; }
    public DateOnly NextWeekStart { get; private set; }
    public DateOnly ThisWeekStart { get; private set; }
    public string WeekLabel { get; private set; } = "";
    public bool HasSlots { get; private set; }

    /// <summary>
    /// Recipe name cache populated from the WeekBag during LoadWeekAsync.
    /// Used by <see cref="ResolveRecipeNameAsync"/> so _WeekGrid.cshtml ghost-cell name resolution
    /// hits the in-memory bag instead of issuing per-recipe GetByIdAsync calls (ADR-021).
    /// </summary>
    private Dictionary<Guid, string> _recipeNameCache = [];

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

    /// <summary>
    /// Server-computed rolled-up fulfillment/cost for each ghost cell, keyed by "date_slotId".
    /// Populated during LoadWeekAsync so the full-grid swap shows enriched ghost cells (P3-6b).
    /// </summary>
    public Dictionary<string, MealFulfillmentVm> GhostEnrichments { get; private set; } = [];

    /// <summary>
    /// Cells that were detected as irreconcilable hard-stance conflicts (C6) during the most recent
    /// generate call. Keyed by "date_slotId". Request-scoped — not stored; on a plain GET reload
    /// without generation this is empty and the cell renders as a plain empty cell.
    /// </summary>
    public Dictionary<string, HardConflictCell> ConflictCells { get; private set; } = [];

    /// <summary>
    /// Cells that were detected as unfulfillable during the most recent generate call — an attendee's
    /// Required tag has ZERO recipes in the full corpus. Keyed by "date_slotId". Request-scoped — not
    /// stored; on a plain GET reload without generation this is empty.
    /// </summary>
    public Dictionary<string, UnfulfillableCell> UnfulfillableCells { get; private set; } = [];

    /// <summary>Advisory insight callouts for the rail, derived from the loaded week (presentation only).</summary>
    public List<InsightCallout> Insights { get; private set; } = [];

    /// <summary>Rolled-up week cost for the budget chip. Null when no pricing data available.</summary>
    public decimal? WeekTotalCost { get; private set; }

    /// <summary>True when any week cost was computed with partial pricing data.</summary>
    public bool WeekCostIsPartial { get; private set; }

    /// <summary>
    /// Projected week cost = confirmed + sum of pending ghost costs (plantry-5lp).
    /// Null when neither confirmed meals nor pending proposals have any pricing data.
    /// </summary>
    public decimal? ProjectedWeekCost { get; private set; }

    /// <summary>
    /// True when the projected cost is an under-estimate (some priced cells, others unpriced
    /// or some ghost cells unpriced/partially priced). Renders the "~$" partial prefix (plantry-5lp).
    /// </summary>
    public bool ProjectedWeekCostIsPartial { get; private set; }

    /// <summary>
    /// Resolved weekly budget target for the viewed week (persisted household setting + per-week override).
    /// Loaded on every request path (GET + all OOB refreshes) by LoadWeekAsync so insights survive
    /// reloads and cell operations. Null = no target = over-budget insight suppressed.
    /// </summary>
    public decimal? WeekBudgetTarget { get; private set; }

    /// <summary>
    /// Resolved planning weights for the viewed week. Null = use PlanningWeights.Default.
    /// Exposed so the Tune popover can reflect persisted values on every render.
    /// </summary>
    public PlanningWeights? WeekPlanningWeights { get; private set; }

    /// <summary>Builds the store key for the pending proposal store: {householdId}_{weekStart:yyyyMMdd}_{sessionId}.</summary>
    private string BuildStoreKey(HouseholdId householdId) =>
        $"{householdId.Value:N}_{WeekStart:yyyyMMdd}_{HttpContext.Session.Id}";

    /// <summary>
    /// Ensures the ASP.NET Core session is started and the .AspNetCore.Session cookie is issued.
    /// ASP.NET Core only emits the session cookie when the session has been WRITTEN — calling
    /// LoadAsync alone does not write to the session, so Session.Id regenerates each request and
    /// the store key differs between the Generate response and the Accept request.
    /// Writing a sentinel byte forces cookie issuance, stabilising Session.Id for the lifetime
    /// of the browser session. Must be called before BuildStoreKey on every planner interaction.
    /// </summary>
    private async Task EnsureSessionStartedAsync(CancellationToken ct = default)
    {
        await HttpContext.Session.LoadAsync(ct);
        // Write a sentinel so the session cookie is issued. The key is intentionally minimal;
        // the value (1 byte = 0x01) is never read by application code — its sole purpose is to
        // cause ASP.NET Core to emit the .AspNetCore.Session cookie on the response.
        if (!HttpContext.Session.TryGetValue("_ps", out _))
            HttpContext.Session.Set("_ps", [0x01]);
    }

    /// <summary>
    /// Island hydration JSON embedded in the page for the meal-planner island.
    /// Contains endpoint URLs + household member data. Set by OnGetAsync.
    /// </summary>
    public string IslandHydrationJson { get; private set; } = "null";

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync(string? week = null, CancellationToken ct = default)
    {
        await LoadWeekAsync(week, ct);
        IslandHydrationJson = BuildIslandHydrationJson();
        return Page();
    }

    private string BuildIslandHydrationJson()
    {
        var members = Members.Select((m, i) => new IslandMemberVm(
            m.UserId.ToString("D"), m.DisplayName, m.Initials, i % 8)).ToList();
        var vm = new IslandHydrationVm(
            AssignUrl: "/MealPlan?handler=AssignJson",
            ClearUrl: "/MealPlan?handler=ClearJson",
            RollupUrl: "/MealPlan?handler=RollupJson",
            EditorJsonUrl: "/MealPlan?handler=EditorJson",
            SearchJsonUrl: "/MealPlan?handler=SearchJson",
            Members: members);
        return JsonSerializer.Serialize(vm, MealPlanHydrationJson.Options);
    }

    // htmx fragment — returns the week grid partial + OOB plan-bar nav so the command
    // bar reflects the newly-loaded week (nav URLs, label, This-week button, budget chip,
    // Auto-fill disabled state all depend on WeekStart which changes on every nav swap).
    public async Task<IActionResult> OnGetGridAsync(string? week = null, CancellationToken ct = default)
    {
        await LoadWeekAsync(week, ct);
        return Partial("_GridWithBarNav", new GridWithBarNavVm(this, BuildPlanBarNavVm(Oob: true)));
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
        return Partial("_GridWithBarNav", new GridWithBarNavVm(this, BuildPlanBarNavVm(Oob: true)));
    }

    // ── AI generate plan POST ────────────────────────────────────────────────

    public async Task<IActionResult> OnPostGenerateAsync(
        string? week = null,
        string? scope = null,
        CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var weekStart = week is not null && DateOnly.TryParse(week, out var parsed)
            ? DomainMealPlan.NormalizeToMonday(parsed)
            : DomainMealPlan.NormalizeToMonday(DateOnly.FromDateTime(DateTime.Today));

        // Ensure session is started and cookie issued so Session.Id is stable across requests.
        await EnsureSessionStartedAsync(ct);

        // Rebuild WeekStart so BuildStoreKey is correct; also resolves persisted budget/weights.
        await LoadWeekAsync(week, ct);

        // PlanningWeights come from the resolved persisted setting (no longer read from form).
        // PlanningWeights only bias SOFT choices — they never relax a hard dietary stance (M5/M11).
        // The constraint resolver from P3-6a stays authoritative.
        var weights = WeekPlanningWeights;

        // Resolve scope date (L2 per-day scope targeting, P3-6b):
        //   • Per-day header buttons post scope=today&week=<that day's ISO date> — derive from week
        //     so clicking "Auto-fill Thursday" fills Thursday, not the live current date.
        //   • Popover "Just today" posts scope=today with no explicit week — fall back to DateTime.Today.
        //   • Whole-week (default): scopeDate stays null (all empty cells in the week).
        DateOnly? scopeDate = null;
        if (scope == "today")
        {
            scopeDate = (week is not null && DateOnly.TryParse(week, out var sd)) ? sd : DateOnly.FromDateTime(DateTime.Today);
        }

        var storeKey = BuildStoreKey(householdId);

        // Merge guard for scoped (per-day) generation:
        //   GeneratePlanService.SetAsync replaces the ENTIRE proposal store. For whole-week generation
        //   that is correct (we want a fresh slate). But for per-day scope (scopeDate non-null) we must
        //   preserve pending proposals on OTHER days, exactly as OnPostRegenerateCellAsync does.
        //   Snapshot the surviving proposals first, then merge after generation.
        IReadOnlyList<ProposedMeal>? otherDayProposals = null;
        if (scopeDate.HasValue)
        {
            var allBefore = await pendingProposalStore.GetAsync(storeKey, ct);
            var scopeKey = scopeDate.Value.ToString("yyyy-MM-dd");
            otherDayProposals = allBefore
                .Where(p => p.Date.ToString("yyyy-MM-dd") != scopeKey)
                .ToList();
        }

        var generateResult = await generatePlanService.ExecuteAsync(householdId, weekStart, storeKey, weights, scopeDate, ct);

        // Re-merge surviving proposals when a per-day scope was used.
        if (scopeDate.HasValue && otherDayProposals is { Count: > 0 })
        {
            var newDayProposals = await pendingProposalStore.GetAsync(storeKey, ct);
            var merged = otherDayProposals.Concat(newDayProposals).ToList();
            await pendingProposalStore.SetAsync(storeKey, merged, ct);
        }

        // Carry hard-conflict cells (C6) and unfulfillable cells so the grid can render in-cell markers.
        ConflictCells = generateResult.Conflicts
            .ToDictionary(c => CellKey(c.Date, c.MealSlotId));
        UnfulfillableCells = generateResult.UnfulfillableCells
            .ToDictionary(u => CellKey(u.Date, u.MealSlotId));

        // Reload to pick up pending proposals; WeekBudgetTarget is resolved inside LoadWeekAsync.
        await LoadWeekAsync(week, ct);
        return Partial("_GridWithBarNav", new GridWithBarNavVm(this, BuildPlanBarNavVm(Oob: true)));
    }

    // ── Accept all proposals POST ────────────────────────────────────────────

    public async Task<IActionResult> OnPostAcceptAllAsync(string? week = null, CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        await EnsureSessionStartedAsync(ct);
        await LoadWeekAsync(week, ct);

        var storeKey = BuildStoreKey(householdId);
        var userId = await GetCurrentUserIdAsync(ct);
        await acceptProposalService.AcceptAllAsync(householdId, WeekStart, storeKey, userId, ct);

        await LoadWeekAsync(week, ct);
        return Partial("_GridWithBarNav", new GridWithBarNavVm(this, BuildPlanBarNavVm(Oob: true)));
    }

    // ── Discard all proposals POST ───────────────────────────────────────────

    public async Task<IActionResult> OnPostDiscardAsync(string? week = null, CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        await EnsureSessionStartedAsync(ct);
        await LoadWeekAsync(week, ct);

        var storeKey = BuildStoreKey(householdId);
        await acceptProposalService.DiscardAsync(storeKey, ct);

        await LoadWeekAsync(week, ct);
        return Partial("_GridWithBarNav", new GridWithBarNavVm(this, BuildPlanBarNavVm(Oob: true)));
    }

    // ── Set planning settings POST ────────────────────────────────────────────
    // Persists the week budget and/or weights as a per-week override (scoped to the
    // currently-viewed week). Returns the full grid+bar so the budget chip and
    // over-budget insight recompute immediately via the existing OOB plumbing.

    public async Task<IActionResult> OnPostSetPlanningSettingsAsync(
        string? week = null,
        [FromForm] decimal? budget = null,
        [FromForm] int? wasteWeight = null,
        [FromForm] int? costWeight = null,
        [FromForm] int? varietyWeight = null,
        CancellationToken ct = default)
    {
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);

        // Normalise week so the override targets the correct Monday.
        var weekStart = week is not null && DateOnly.TryParse(week, out var parsed)
            ? DomainMealPlan.NormalizeToMonday(parsed)
            : DomainMealPlan.NormalizeToMonday(DateOnly.FromDateTime(DateTime.Today));

        // Resolve the submitted budget: positive value → Money; zero or null → clear.
        Money? budgetMoney = budget is > 0
            ? Money.FromDecimal(budget.Value, "USD")
            : null;

        // Resolve the submitted weights; ignore if they don't sum to 100.
        PlanningWeights? weights = null;
        if (wasteWeight.HasValue && costWeight.HasValue && varietyWeight.HasValue)
        {
            try { weights = new PlanningWeights(wasteWeight.Value, costWeight.Value, varietyWeight.Value); }
            catch (ArgumentException) { /* Invalid — fall back to null (no override) */ }
        }

        await setPlanningSettingsService.ExecuteAsync(householdId, weekStart, budgetMoney, weights, ct);

        // Reload so the resolved budget + insights recompute from DB truth.
        await LoadWeekAsync(week, ct);
        return Partial("_GridWithBarNav", new GridWithBarNavVm(this, BuildPlanBarNavVm(Oob: true)));
    }

    // ── Accept single cell POST ──────────────────────────────────────────────

    public async Task<IActionResult> OnPostAcceptCellAsync(
        string date, Guid slotId, string? week = null, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(slotId);
        await EnsureSessionStartedAsync(ct);
        // LoadWeekAsync must be called before BuildStoreKey so WeekStart is set correctly.
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);

        var storeKey = BuildStoreKey(householdId);
        var userId = await GetCurrentUserIdAsync(ct);
        await acceptProposalService.AcceptCellAsync(householdId, parsedDate, sid, storeKey, userId, ct);

        // Return the full week grid so the pending bar count is always fresh (pending bar lives
        // inside _WeekGrid, so a cell-only swap would leave it stale after per-cell operations).
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);
        return Partial("_GridWithBarNav", new GridWithBarNavVm(this, BuildPlanBarNavVm(Oob: true)));
    }

    // ── Reject single cell POST ──────────────────────────────────────────────

    public async Task<IActionResult> OnPostRejectCellAsync(
        string date, Guid slotId, string? week = null, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(slotId);
        await EnsureSessionStartedAsync(ct);
        // LoadWeekAsync must be called before BuildStoreKey so WeekStart is set correctly.
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);

        var storeKey = BuildStoreKey(householdId);
        await acceptProposalService.RejectCellAsync(storeKey, parsedDate, sid, ct);

        // Return the full week grid so the pending bar count is always fresh.
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);
        return Partial("_GridWithBarNav", new GridWithBarNavVm(this, BuildPlanBarNavVm(Oob: true)));
    }

    // ── Regenerate single cell POST (P3-6b, J8) ─────────────────────────────
    // Re-proposes ONE pending cell. Removes the existing proposal for this cell from the store,
    // generates a fresh proposal for just this cell, merges it back with the surviving proposals
    // for other cells (GeneratePlanService.SetAsync replaces the entire store key — to preserve
    // other pending cells we snapshot before, then upsert after), then routes the response
    // through CellFragmentAsync so #plan-rail is re-emitted OOB (ADR-013 / OobContract).
    // Only the one pending cell changes — all other confirmed meals and pending ghosts are untouched.

    public async Task<IActionResult> OnPostRegenerateCellAsync(
        string date, Guid slotId, string? week = null, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(slotId);
        await EnsureSessionStartedAsync(ct);
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);

        var storeKey = BuildStoreKey(householdId);

        // 1. Snapshot the OTHER pending proposals before any mutation, so they can be merged back
        //    after regeneration. GeneratePlanService.SetAsync replaces the whole store — without
        //    this merge step every other pending ghost in the week would be silently destroyed.
        var allBefore = await pendingProposalStore.GetAsync(storeKey, ct);
        var cellKey = CellKey(parsedDate, sid);
        var otherPending = allBefore
            .Where(p => CellKey(p.Date, p.MealSlotId) != cellKey)
            .ToList();

        // 2. Remove the old proposal for this cell so the planner proposes a fresh one.
        await pendingProposalStore.RemoveAsync(storeKey, parsedDate, sid, ct);

        // 3. Re-run generation scoped to just this one cell (scopeDate + single-slot).
        //    Uses default weights — regeneration is an in-grid action, not popover-driven.
        await generatePlanService.ExecuteAsync(
            householdId,
            DomainMealPlan.NormalizeToMonday(parsedDate),
            storeKey,
            weights: null,
            scopeDate: parsedDate,
            ct);

        // 4. Merge: read the newly-staged proposal(s) for this cell, then SetAsync
        //    the union of other-cell proposals + new cell proposal(s).
        var newCellProposals = await pendingProposalStore.GetAsync(storeKey, ct);
        var merged = otherPending.Concat(newCellProposals).ToList();
        await pendingProposalStore.SetAsync(storeKey, merged, ct);

        // 5. Reload so CellFragmentAsync picks up the updated proposals.
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);

        // Return the cell + OOB rail (ADR-013 — same path as Assign/Clear/Accept).
        return await CellFragmentAsync(householdId, parsedDate, sid, ct);
    }

    // ── Generate single (empty) cell POST (P3-6b, scope: single meal) ────────
    // Per-cell "Auto-fill" on an EMPTY cell. Mirrors OnPostRegenerateCellAsync, but the cell has
    // no existing proposal to remove. Generation is scoped to this cell's date (ExecuteAsync fills
    // ALL empty cells on scopeDate), so we keep only the newly-staged proposal for THIS cell and
    // merge it back with every pre-existing proposal — generating one cell never disturbs the rest.
    // Routes through CellFragmentAsync so #plan-rail is re-emitted OOB (ADR-013 / OobContract).

    public async Task<IActionResult> OnPostGenerateCellAsync(
        string date, Guid slotId, string? week = null, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(slotId);
        await EnsureSessionStartedAsync(ct);
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);

        var storeKey = BuildStoreKey(householdId);

        // 1. Snapshot every pending proposal that is NOT this cell, so they survive the
        //    SetAsync replacement. The cell is empty, so normally nothing matches this cell —
        //    the filter is defensive (idempotent if the cell already had a proposal).
        var allBefore = await pendingProposalStore.GetAsync(storeKey, ct);
        var cellKey = CellKey(parsedDate, sid);
        var otherPending = allBefore
            .Where(p => CellKey(p.Date, p.MealSlotId) != cellKey)
            .ToList();

        // 2. Generate scoped to this cell's date (default weights — in-grid action, not popover-driven).
        await generatePlanService.ExecuteAsync(
            householdId,
            DomainMealPlan.NormalizeToMonday(parsedDate),
            storeKey,
            weights: null,
            scopeDate: parsedDate,
            ct);

        // 3. ExecuteAsync fills ALL empty cells on the date; keep ONLY the new proposal for THIS
        //    cell, then merge with the surviving snapshot so other cells are untouched.
        var generated = await pendingProposalStore.GetAsync(storeKey, ct);
        var thisCellProposal = generated
            .Where(p => CellKey(p.Date, p.MealSlotId) == cellKey)
            .ToList();
        var merged = otherPending.Concat(thisCellProposal).ToList();
        await pendingProposalStore.SetAsync(storeKey, merged, ct);

        // 4. Reload so CellFragmentAsync picks up the updated proposals.
        await LoadWeekAsync(week ?? DomainMealPlan.NormalizeToMonday(parsedDate).ToString("yyyy-MM-dd"), ct);

        // Return the cell + OOB rail (ADR-013 — same path as Assign/Clear/Accept/Regenerate).
        return await CellFragmentAsync(householdId, parsedDate, sid, ct);
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

    // ── Island JSON endpoints (ADR-015 amendment: island data endpoints return JSON) ──

    /// <summary>
    /// Returns the editor hydration JSON for the meal-planner island.
    /// Called by window.__mealPlannerIsland.openEditor() when a cell or card triggers the editor.
    /// Returns editor state as JSON for the meal-planner island.
    /// </summary>
    public async Task<IActionResult> OnGetEditorJsonAsync(
        string date, Guid slotId, Guid? mealId = null, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var slot = await GetSlotAsync(householdId, MealSlotId.From(slotId), ct);
        if (slot is null) return NotFound();

        Members = (await memberReader.ListMembersAsync(ct)).ToList();

        MealEditorVm? vm = null;
        if (mealId.HasValue)
        {
            var plan = await mealPlanRepo.FindByWeekAsync(
                householdId, DomainMealPlan.NormalizeToMonday(parsedDate), ct);

            var meal = plan?.PlannedMeals.FirstOrDefault(m => m.Id.Value == mealId.Value);
            if (meal is not null)
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var dishes = new List<EditorDishVm>();
                foreach (var d in meal.PlannedDishes.OrderBy(d => d.Ordinal))
                {
                    if (d.RecipeId.HasValue)
                    {
                        var r = await recipeReader.GetByIdAsync(d.RecipeId.Value, ct);
                        var enr = await recipeReader.GetEnrichmentAsync(d.RecipeId.Value, d.Servings, today, ct);
                        decimal? costPerServing = enr?.TotalCost is { } total && d.Servings > 0 ? total / d.Servings : null;
                        dishes.Add(new EditorDishVm(DishKind.Recipe, d.RecipeId.Value, r?.Name ?? "Unknown recipe",
                            d.Servings, d.Ordinal, enr?.FulfillmentPercent, costPerServing, r?.HasPhoto ?? false));
                    }
                    else if (d.ProductId.HasValue)
                    {
                        var names = await catalogReader.ResolveNamesAsync([d.ProductId.Value], ct);
                        dishes.Add(new EditorDishVm(DishKind.Product, d.ProductId.Value,
                            names.GetValueOrDefault(d.ProductId.Value, "Unknown product"), d.Servings, d.Ordinal));
                    }
                }

                int? initFulfillment = null;
                decimal? initCost = null;
                bool initCostPartial = false;
                if (meal.Note is null && meal.PlannedDishes.Count > 0)
                {
                    var initFulfillmentResult = await fulfillmentService.RollUpMealAsync(meal, today, ct);
                    var initCostResult = await costingService.RollUpMealAsync(meal, ct);
                    initFulfillment = initFulfillmentResult.FulfillmentPercent;
                    initCost = initCostResult.Amount;
                    initCostPartial = initCostResult.Completeness == CostCompleteness.Partial;
                }

                vm = new MealEditorVm(meal.Id.Value, parsedDate, slot.Label, slot.DefaultAttendees,
                    meal.AttendeesOverride, meal.Note, dishes, IsEditing: true,
                    InitialFulfillmentPercent: initFulfillment, InitialTotalCost: initCost, InitialCostIsPartial: initCostPartial);
            }
        }

        vm ??= new MealEditorVm(null, parsedDate, slot.Label, slot.DefaultAttendees,
            null, null, [], IsEditing: false);

        var defaultAtt = slot.DefaultAttendees;
        var currentAtt = vm.AttendeesOverride ?? defaultAtt;
        var isOverridden = vm.AttendeesOverride is not null;
        var isNote = vm.Note is not null;

        // Build server-rendered initial rollup HTML (only for existing dish meals)
        string? initialRollupHtml = null;
        if (!isNote && vm.Dishes.Count > 0 && vm.InitialFulfillmentPercent.HasValue)
        {
            var rollupVm = new EditorRollupVm(
                IsNote: false, HasDishes: true,
                FulfillmentPercent: vm.InitialFulfillmentPercent,
                TotalCost: vm.InitialTotalCost,
                CostIsPartial: vm.InitialCostIsPartial);
            initialRollupHtml = await RenderPartialToStringAsync("_EditorRollup", rollupVm, ct);
        }
        else if (isNote)
        {
            var rollupVm = new EditorRollupVm(IsNote: true, HasDishes: false);
            initialRollupHtml = await RenderPartialToStringAsync("_EditorRollup", rollupVm, ct);
        }

        var today2 = DateOnly.FromDateTime(DateTime.Today);
        var dow = parsedDate.DayOfWeek.ToString()[..3];
        var monthDay = parsedDate.ToString("MMM d");

        var dishHydration = vm.Dishes
            .Select(d => new EditorDishHydrationVm(
                d.Kind.ToString().ToLower(),
                d.ItemId.ToString("D"),
                d.Name,
                d.Servings,
                d.FulfillmentPercent,
                d.CostPerServing,
                d.HasPhoto))
            .ToList();

        var payload = new MealEditorHydrationVm(
            DateStr: date,
            SlotIdStr: slot.Id.Value.ToString("D"),
            SlotLabel: slot.Label,
            MealId: vm.MealId?.ToString("D"),
            IsEditing: vm.IsEditing,
            Mode: isNote ? "note" : "dishes",
            Note: vm.Note ?? "",
            Dishes: dishHydration,
            Att: currentAtt.Select(x => x.ToString("D")).ToList(),
            DefaultAtt: defaultAtt.Select(x => x.ToString("D")).ToList(),
            AttOverridden: isOverridden,
            InitialRollupHtml: initialRollupHtml,
            DateDowLabel: dow,
            DateMonthDay: monthDay,
            IsToday: parsedDate == today2);

        return new JsonResult(payload, MealPlanHydrationJson.Options);
    }

    /// <summary>
    /// Assign a meal from the island editor (JSON body → JSON cell+rail+bar response).
    /// ADR-015 amendment: island data endpoints return JSON.
    /// The response JSON carries cellHtml, railHtml, barNavHtml — the island swaps them
    /// into the live DOM, preserving the ADR-013 OOB contract (rail recomputes on every mutation).
    /// </summary>
    public async Task<IActionResult> OnPostAssignJsonAsync(CancellationToken ct = default)
    {
        AssignJsonInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<AssignJsonInput>(
                Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch { return new JsonResult(new { error = "Invalid request body." }) { StatusCode = 400 }; }

        if (input is null) return new JsonResult(new { error = "Invalid request body." }) { StatusCode = 400 };
        if (!DateOnly.TryParse(input.Date, out var parsedDate))
            return new JsonResult(new { error = "Invalid date." }) { StatusCode = 400 };

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var userId = await GetCurrentUserIdAsync(ct);
        var sid = MealSlotId.From(input.SlotId);
        var mid = input.MealId.HasValue ? PlannedMealId.From(input.MealId.Value) : (PlannedMealId?)null;

        List<Guid>? overrideList = input.AttendeesOverridden ? input.Att : null;

        string? hardStanceWarning = null;
        if (input.Mode == "note")
        {
            if (string.IsNullOrWhiteSpace(input.Note))
                return new JsonResult(new { error = "Note is required." }) { StatusCode = 400 };
            var noteResult = await assignService.AssignNoteAsync(householdId, parsedDate, sid, input.Note!, overrideList, userId, mid, ct);
            hardStanceWarning = noteResult.HardStanceWarning;
        }
        else
        {
            var specs = BuildDishSpecsFromJson(input.Dishes);
            if (specs.Count == 0)
                return new JsonResult(new { error = "At least one dish is required." }) { StatusCode = 400 };
            var dishResult = await assignService.AssignDishesAsync(householdId, parsedDate, sid, specs, overrideList, userId, mid, ct);
            hardStanceWarning = dishResult.HardStanceWarning;
        }

        return await CellMutationJsonAsync(householdId, parsedDate, sid, hardStanceWarning, ct);
    }

    /// <summary>
    /// Clear a meal from the island editor (JSON body → JSON cell+rail+bar response).
    /// ADR-015 amendment: island data endpoints return JSON.
    /// </summary>
    public async Task<IActionResult> OnPostClearJsonAsync(CancellationToken ct = default)
    {
        ClearJsonInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<ClearJsonInput>(
                Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch { return new JsonResult(new { error = "Invalid request body." }) { StatusCode = 400 }; }

        if (input is null || !DateOnly.TryParse(input.Date, out var parsedDate))
            return new JsonResult(new { error = "Invalid request body." }) { StatusCode = 400 };

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(input.SlotId);
        await assignService.ClearMealAsync(householdId, parsedDate, PlannedMealId.From(input.MealId), ct);

        return await CellMutationJsonAsync(householdId, parsedDate, sid, null, ct);
    }

    /// <summary>
    /// Rollup projection for the island editor (JSON body → JSON { html } response).
    /// ADR-013 §4/§5, ADR-020 §7: fulfillment/cost is a server projection.
    /// The island posts the draft dish list here on every change and renders the returned HTML
    /// into the rollup container. No client-side formula.
    /// </summary>
    public async Task<IActionResult> OnPostRollupJsonAsync(CancellationToken ct = default)
    {
        RollupJsonInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<RollupJsonInput>(
                Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch { return new JsonResult(new { error = "Invalid request body." }) { StatusCode = 400 }; }

        if (input is null) return new JsonResult(new { error = "Invalid request body." }) { StatusCode = 400 };

        if (input.Mode == "note")
        {
            var html = await RenderPartialToStringAsync("_EditorRollup",
                new EditorRollupVm(IsNote: true, HasDishes: false), ct);
            return new JsonResult(new { html });
        }

        var specs = BuildDishSpecsFromJson(input.Dishes);
        if (specs.Count == 0)
        {
            var html = await RenderPartialToStringAsync("_EditorRollup",
                new EditorRollupVm(IsNote: false, HasDishes: false), ct);
            return new JsonResult(new { html });
        }

        // Build a transient in-memory meal for rollup projection only.
        // No SaveChangesAsync → no DB write.
        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Use today as date for the rollup (not date-specific since we just need fulfillment)
        var rollupDate = today;
        var rollupSid = MealSlotId.From(Guid.NewGuid()); // ephemeral slot id — rollup only
        var tempPlan = DomainMealPlan.Start(householdId, DomainMealPlan.NormalizeToMonday(rollupDate), clock);
        tempPlan.AssignMeal(rollupDate, rollupSid, specs, null, "rollup-preview", Guid.Empty, clock);
        var meal = tempPlan.PlannedMeals.FirstOrDefault(m => m.MealSlotId == rollupSid);

        if (meal is null)
        {
            var html = await RenderPartialToStringAsync("_EditorRollup",
                new EditorRollupVm(IsNote: false, HasDishes: true), ct);
            return new JsonResult(new { html });
        }

        var fulfillment = await fulfillmentService.RollUpMealAsync(meal, today, ct);
        var mealCost = await costingService.RollUpMealAsync(meal, ct);

        var resultHtml = await RenderPartialToStringAsync("_EditorRollup",
            new EditorRollupVm(
                IsNote: false, HasDishes: true,
                FulfillmentPercent: fulfillment.FulfillmentPercent,
                TotalCost: mealCost.Amount,
                CostIsPartial: mealCost.Completeness == CostCompleteness.Partial), ct);
        return new JsonResult(new { html = resultHtml });
    }

    /// <summary>
    /// Dish search for the island editor — returns JSON results rendered in-component by the island.
    /// </summary>
    public async Task<IActionResult> OnGetSearchJsonAsync(string q, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var recipes = await recipeReader.SearchAsync(q, 6, ct);

        var hits = new List<object>(recipes.Count);
        foreach (var r in recipes)
        {
            var enr = await recipeReader.GetEnrichmentAsync(r.RecipeId, r.DefaultServings, today, ct);
            decimal? costPerServing = enr?.TotalCost is { } total && r.DefaultServings > 0
                ? total / r.DefaultServings : null;
            hits.Add(new
            {
                kind = "recipe",
                itemId = r.RecipeId.ToString("D"),
                name = r.Name,
                defaultServings = r.DefaultServings,
                fulfillmentPercent = (object?)enr?.FulfillmentPercent,
                costPerServing = (object?)costPerServing,
                hasPhoto = r.HasPhoto,
                photoUrl = r.HasPhoto ? $"/Recipes/Details?id={r.RecipeId}&handler=Photo" : null,
            });
        }

        var products = await catalogReader.SearchAsync(q, 5, ct);
        foreach (var p in products)
        {
            hits.Add(new
            {
                kind = "product",
                itemId = p.ProductId.ToString("D"),
                name = p.Name,
                defaultServings = 1,
                fulfillmentPercent = (object?)null,
                costPerServing = (object?)null,
                hasPhoto = false,
                photoUrl = (object?)null,
            });
        }

        return new JsonResult(new { hits });
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

        // Resolve persisted planning settings (budget + weights) for the viewed week.
        // Runs on every request path (GET + all OOB refreshes) so WeekBudgetTarget is always populated.
        await LoadPlanningSettingsAsync(householdId, ct);

        // Load planned meals for this week.
        DomainMealPlan? loadedPlan = null;
        if (HasSlots)
        {
            loadedPlan = await mealPlanRepo.FindByWeekAsync(householdId, WeekStart, ct);
        }

        // Load pending AI proposals for this week from the store (needed before read model load
        // so ghost-cell recipe IDs are included in the flat query).
        await LoadPendingProposalsAsync(householdId, ct);

        // ── ADR-021 flat read model load ──────────────────────────────────────
        // Gather all distinct recipe/product IDs from both planned meals AND pending proposals
        // so the read model can load everything in a single pass.
        var recipeIds = new HashSet<Guid>();
        var productIds = new HashSet<Guid>();

        if (loadedPlan is not null)
        {
            foreach (var meal in loadedPlan.PlannedMeals)
            foreach (var dish in meal.PlannedDishes)
            {
                if (dish.RecipeId.HasValue) recipeIds.Add(dish.RecipeId.Value);
                else if (dish.ProductId.HasValue) productIds.Add(dish.ProductId.Value);
            }
        }

        foreach (var proposal in PendingProposals.Values)
        foreach (var dish in proposal.Dishes)
        {
            recipeIds.Add(dish.RecipeId);
        }

        var bag = await weekReadModel.LoadAsync(recipeIds.ToList(), productIds.ToList(), ct);
        var enricher = new WeekBagEnricher(bag, recipesFulfillmentService, recipesCostingService, clock);

        // Populate recipe name cache so _WeekGrid.cshtml ghost-cell name resolution
        // uses the in-memory bag instead of per-recipe GetByIdAsync calls (ADR-021).
        _recipeNameCache = bag.Recipes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name);

        // ── Enrich planned meals and build MealsByCell ────────────────────────
        if (loadedPlan is not null)
        {
            // Accumulate per-meal cost to derive week total by summation (no separate RollUpWeekAsync).
            decimal weekCostTotal = 0m;
            bool weekCostAnyPriced = false;
            bool weekCostAnyPartial = false;
            bool weekCostAnyUnpriced = false;

            foreach (var meal in loadedPlan.PlannedMeals.OrderBy(m => m.Ordinal))
            {
                var key = CellKey(meal.Date, meal.MealSlotId);
                if (!MealsByCell.TryGetValue(key, out var list))
                {
                    list = [];
                    MealsByCell[key] = list;
                }

                // Resolve dish names in ordinal order.
                // Recipe names come from the in-memory bag (no DB call).
                // Product dish names are batch-resolved via catalogReader (product dishes are
                // rare and not on the O(meals×dishes×ingredients) hot path).
                var productDishIds = meal.PlannedDishes
                    .Where(d => d.ProductId.HasValue)
                    .Select(d => d.ProductId!.Value)
                    .Distinct()
                    .ToList();

                IReadOnlyDictionary<Guid, string> productNames =
                    productDishIds.Count > 0
                        ? await catalogReader.ResolveNamesAsync(productDishIds, ct)
                        : new Dictionary<Guid, string>();

                var dishNames = meal.PlannedDishes
                    .OrderBy(d => d.Ordinal)
                    .Select(d =>
                    {
                        if (d.RecipeId.HasValue)
                            return enricher.GetRecipeName(d.RecipeId.Value) ?? "Unknown recipe";
                        if (d.ProductId.HasValue)
                            return productNames.GetValueOrDefault(d.ProductId.Value, "Unknown product");
                        return "Unknown";
                    })
                    .ToList();

                // Compute per-meal fulfillment and cost enrichment using the bag enricher
                // (memoized per (recipeId, servings) — a recipe appearing in multiple cells
                // is computed exactly once).
                MealFulfillmentVm? enrichment = null;
                if (meal.Note is null && meal.PlannedDishes.Count > 0)
                {
                    int totalFulfillmentPct = 0;
                    bool hasExpiring = false;
                    decimal? mealCostAmount = null;
                    bool mealCostPartial = false;
                    bool mealAnyPriced = false;
                    bool mealAnyUnpriced = false;

                    foreach (var dish in meal.PlannedDishes)
                    {
                        if (dish.RecipeId.HasValue)
                        {
                            var dishEnr = enricher.Enrich(dish.RecipeId.Value, dish.Servings, today);
                            if (dishEnr is not null)
                            {
                                totalFulfillmentPct += dishEnr.FulfillmentPercent;
                                if (dishEnr.HasExpiringIngredients) hasExpiring = true;
                                if (dishEnr.TotalCost.HasValue)
                                {
                                    mealCostAmount = (mealCostAmount ?? 0m) + dishEnr.TotalCost.Value;
                                    mealAnyPriced = true;
                                    if (dishEnr.CostIsPartial) mealCostPartial = true;
                                }
                                else
                                {
                                    mealAnyUnpriced = true;
                                }
                            }
                            else
                            {
                                // Recipe not in bag (archived?) — treat as 0% / unpriced.
                                mealAnyUnpriced = true;
                            }
                        }
                        else if (dish.ProductId.HasValue)
                        {
                            // Product dishes: fulfillment via existing MealPlanStockReader path;
                            // cost via existing MealPlanPriceReader path. These are rare —
                            // not on the high-fan-out recipe path — so port calls are acceptable.
                            var singleDishMeal = MakeSingleDishMeal(householdId, meal.Date, meal.MealSlotId, dish);
                            if (singleDishMeal is not null)
                            {
                                var dishFulfillment = await fulfillmentService.RollUpMealAsync(singleDishMeal, today, ct);
                                var dishCost = await costingService.RollUpMealAsync(singleDishMeal, ct);

                                totalFulfillmentPct += dishFulfillment.FulfillmentPercent;
                                if (dishFulfillment.HasExpiringIngredients) hasExpiring = true;
                                if (dishCost.Amount.HasValue)
                                {
                                    mealCostAmount = (mealCostAmount ?? 0m) + dishCost.Amount.Value;
                                    mealAnyPriced = true;
                                    if (dishCost.Completeness == CostCompleteness.Partial) mealCostPartial = true;
                                }
                                else
                                {
                                    mealAnyUnpriced = true;
                                }
                            }
                            else
                            {
                                mealAnyUnpriced = true;
                            }
                        }
                    }

                    int dishCount = meal.PlannedDishes.Count;
                    var mealFulfillPct = dishCount > 0 ? (int)Math.Round((double)totalFulfillmentPct / dishCount) : 0;

                    enrichment = new MealFulfillmentVm(
                        mealFulfillPct,
                        hasExpiring,
                        mealCostAmount,
                        mealCostPartial || (mealAnyPriced && mealAnyUnpriced));

                    // Accumulate into week cost totals.
                    if (mealCostAmount.HasValue)
                    {
                        weekCostTotal += mealCostAmount.Value;
                        weekCostAnyPriced = true;
                        if (mealCostPartial || (mealAnyPriced && mealAnyUnpriced))
                            weekCostAnyPartial = true;
                    }
                    else if (dishCount > 0 && meal.Note is null)
                    {
                        weekCostAnyUnpriced = true;
                    }
                }

                list.Add(new MealCellVm(meal.Id.Value, meal.Note, dishNames, meal.AttendeesOverride ?? [], enrichment));
            }

            // Derive week total from summed per-meal results (no separate RollUpWeekAsync pass).
            if (weekCostAnyPriced)
            {
                WeekTotalCost = weekCostTotal;
                WeekCostIsPartial = weekCostAnyPartial || (weekCostAnyPriced && weekCostAnyUnpriced);
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

        // Compute rolled-up fulfillment/cost for all ghost cells (P3-6b enriched ghost cells)
        // using the same enricher (memoized — shared with committed-meal enrichment above).
        await LoadGhostEnrichmentsAsync(enricher, ct);

        // Compute projected week cost = confirmed + sum of pending ghost costs (plantry-5lp).
        // Runs AFTER LoadGhostEnrichmentsAsync so GhostEnrichments is fully populated.
        {
            decimal sum = WeekTotalCost ?? 0m;
            bool anyConfirmed = WeekTotalCost.HasValue;
            bool projectedPartial = WeekCostIsPartial;
            bool anyPending = false;
            foreach (var g in GhostEnrichments.Values)
            {
                if (g.TotalCost.HasValue)
                {
                    sum += g.TotalCost.Value;
                    anyPending = true;
                    if (g.CostIsPartial) projectedPartial = true;
                }
                else
                {
                    // An unpriced pending cell makes the projection an under-estimate.
                    projectedPartial = true;
                }
            }
            ProjectedWeekCost = (anyConfirmed || anyPending) ? sum : (decimal?)null;
            ProjectedWeekCostIsPartial = projectedPartial && ProjectedWeekCost.HasValue;
        }

        await BuildInsightsAsync(loadedPlan, emptyCells, ct);
    }

    /// <summary>
    /// Builds a single-dish transient PlannedMeal for product-dish fulfillment/cost
    /// computation via the existing port-backed services. Product dishes are rare (not on the
    /// recipe×ingredient hot path), so a port call per product dish is acceptable.
    /// Returns null when the aggregate does not produce a meal (should not occur in practice).
    /// </summary>
    private PlannedMeal? MakeSingleDishMeal(
        HouseholdId householdId,
        DateOnly date,
        MealSlotId slotId,
        PlannedDish dish)
    {
        var tempPlan = DomainMealPlan.Start(householdId, DomainMealPlan.NormalizeToMonday(date), clock);
        var spec = dish.ProductId.HasValue
            ? new DishSpec(DishKind.Product, dish.ProductId.Value, dish.Servings)
            : new DishSpec(DishKind.Recipe, dish.RecipeId!.Value, dish.Servings);
        tempPlan.AssignMeal(date, slotId, [spec], null, "rollup-single-dish", Guid.Empty, clock);
        return tempPlan.PlannedMeals.FirstOrDefault(m => m.Date == date && m.MealSlotId == slotId);
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
            budgetTarget: WeekBudgetTarget, // user-entered from tune popover (null when not provided)
            priorPlans: null,               // prior plan history not loaded — suppresses vs-history repetition rule
            today,
            ct);

        Insights = insights.Insights
            .Select(i => new InsightCallout(i.Tone, i.Icon, i.Title, i.Body, i.ActionUrl))
            .ToList();
    }

    /// <summary>
    /// Resolves the effective planning settings for the viewed week by merging the household
    /// default with the per-week override. Populates WeekBudgetTarget and WeekPlanningWeights.
    /// Called on every request path (GET + all OOB refreshes) so the budget chip and insights
    /// survive reloads, navigation, and cell operations.
    /// </summary>
    private async Task LoadPlanningSettingsAsync(HouseholdId householdId, CancellationToken ct)
    {
        var settings = await planningSettingsRepo.FindByHouseholdAsync(householdId, ct);
        var weekOverride = await weekOverrideRepo.FindAsync(householdId, WeekStart, ct);
        var (budget, weights) = PlanningSettingsResolver.Resolve(settings, weekOverride);

        WeekBudgetTarget = budget?.ToDecimal();
        WeekPlanningWeights = weights;
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

    /// <summary>
    /// Computes rolled-up fulfillment/cost for all pending ghost cells (P3-6b, ADR-013 §4/§5)
    /// using the pre-loaded <paramref name="enricher"/> (no further DB round-trips per ghost cell).
    /// Best-effort: failures are silently swallowed so a broken enrichment never hides the ghost cell.
    /// </summary>
    private Task LoadGhostEnrichmentsAsync(WeekBagEnricher enricher, CancellationToken ct)
    {
        GhostEnrichments = [];
        if (PendingProposals.Count == 0) return Task.CompletedTask;

        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var (key, proposal) in PendingProposals)
        {
            var enrichment = BuildGhostEnrichmentFromBag(enricher, proposal, today);
            if (enrichment is not null) GhostEnrichments[key] = enrichment;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Computes ghost-cell enrichment from the pre-loaded bag (pure, no IO).
    /// Best-effort: returns null on an empty proposal or when no recipe facts are in the bag.
    /// </summary>
    private MealFulfillmentVm? BuildGhostEnrichmentFromBag(
        WeekBagEnricher enricher, ProposedMeal pending, DateOnly today)
    {
        if (pending.Dishes.Count == 0) return null;
        try
        {
            int totalFulfillPct = 0;
            bool hasExpiring = false;
            decimal? totalCost = null;
            bool anyPartial = false;
            bool anyPriced = false;
            bool anyUnpriced = false;

            foreach (var dish in pending.Dishes)
            {
                var enr = enricher.Enrich(dish.RecipeId, dish.Servings, today);
                if (enr is null) continue;

                totalFulfillPct += enr.FulfillmentPercent;
                if (enr.HasExpiringIngredients) hasExpiring = true;
                if (enr.TotalCost.HasValue)
                {
                    totalCost = (totalCost ?? 0m) + enr.TotalCost.Value;
                    anyPriced = true;
                    if (enr.CostIsPartial) anyPartial = true;
                }
                else
                {
                    anyUnpriced = true;
                }
            }

            var dishCount = pending.Dishes.Count;
            var avgFulfill = dishCount > 0 ? (int)Math.Round((double)totalFulfillPct / dishCount) : 0;

            // Mirrors MealCost.Aggregate: partial when any dish has a partial cost OR when
            // some dishes are priced and others are not (under-estimate of the true total).
            var costIsPartial = anyPartial || (anyPriced && anyUnpriced);
            return new MealFulfillmentVm(avgFulfill, hasExpiring, totalCost, costIsPartial);
        }
        catch (Exception ex)
        {
            // Enrichment is best-effort — degrade gracefully if computation fails.
            logger.LogWarning(ex,
                "Ghost enrichment (bag path) failed for household {HouseholdId}, recipes {RecipeIds}; degrading to no-overlay",
                tenant.HouseholdId,
                string.Join(",", pending.Dishes.Select(d => d.RecipeId)));
            return null;
        }
    }

    /// <summary>
    /// Computes ghost-cell enrichment using the port-backed services (fallback path for
    /// CellMutationJsonAsync / CellFragmentAsync which may not have a bag enricher available).
    /// Builds a transient meal from the proposal's dishes and runs the P3-4 services.
    /// Best-effort: returns null on an empty proposal or any roll-up failure.
    /// Single source of truth for the three places that need ghost enrichment from the old path.
    /// </summary>
    private async Task<MealFulfillmentVm?> BuildGhostEnrichmentAsync(
        HouseholdId householdId, ProposedMeal pending, CancellationToken ct)
    {
        if (pending.Dishes.Count == 0) return null;
        try
        {
            var tempPlan = DomainMealPlan.Start(householdId, DomainMealPlan.NormalizeToMonday(pending.Date), clock);
            var dishSpecs = pending.Dishes.OrderBy(x => x.Ordinal)
                .Select(d => new DishSpec(DishKind.Recipe, d.RecipeId, d.Servings))
                .ToList();
            tempPlan.AssignMeal(pending.Date, pending.MealSlotId, dishSpecs, null, "rollup-ghost", Guid.Empty, clock);
            var tempMeal = tempPlan.PlannedMeals.FirstOrDefault(m => m.Date == pending.Date && m.MealSlotId == pending.MealSlotId);
            if (tempMeal is null) return null;

            var today = DateOnly.FromDateTime(DateTime.Today);
            var fulfillment = await fulfillmentService.RollUpMealAsync(tempMeal, today, ct);
            var mealCost = await costingService.RollUpMealAsync(tempMeal, ct);
            return new MealFulfillmentVm(
                fulfillment.FulfillmentPercent, fulfillment.HasExpiringIngredients,
                mealCost.Amount, mealCost.Completeness == CostCompleteness.Partial);
        }
        catch (Exception ex)
        {
            // Enrichment is best-effort — degrade gracefully if roll-up fails.
            logger.LogWarning(ex,
                "Ghost enrichment (async path) failed for household {HouseholdId}, recipes {RecipeIds}; degrading to no-overlay",
                householdId,
                string.Join(",", pending.Dishes.Select(d => d.RecipeId)));
            return null;
        }
    }

    /// <summary>Resolves a pending proposal's dish recipe names for ghost-cell display (the cell
    /// fragment must resolve these the same way _WeekGrid does inline).</summary>
    private async Task<IReadOnlyList<string>> ResolveGhostDishNamesAsync(ProposedMeal pending, CancellationToken ct)
    {
        var names = new List<string>(pending.Dishes.Count);
        foreach (var d in pending.Dishes.OrderBy(x => x.Ordinal))
        {
            var r = await recipeReader.GetByIdAsync(d.RecipeId, ct);
            names.Add(r?.Name ?? "Unknown recipe");
        }
        return names;
    }

    /// <summary>
    /// Renders a cell mutation result as JSON for the island JSON endpoints (AssignJson/ClearJson).
    /// The response carries cellHtml + railHtml + barNavHtml so the island can swap each
    /// fragment into the live DOM, preserving the ADR-013 OOB contract (rail recomputes on
    /// every mutation). Equivalent to CellFragmentAsync but returns JSON instead of HTML.
    /// </summary>
    private async Task<IActionResult> CellMutationJsonAsync(
        HouseholdId householdId, DateOnly date, MealSlotId slotId, string? hardStanceWarning, CancellationToken ct)
    {
        await LoadWeekAsync(DomainMealPlan.NormalizeToMonday(date).ToString("yyyy-MM-dd"), ct);
        var key = CellKey(date, slotId);
        var meals = MealsByCell.GetValueOrDefault(key) ?? [];
        var slot = Slots.FirstOrDefault(s => s.Id == slotId);
        var pending = PendingProposals.GetValueOrDefault(key);

        IReadOnlyList<string>? ghostDishNames = null;
        MealFulfillmentVm? ghostEnrichment = null;
        if (pending is not null)
        {
            ghostDishNames = await ResolveGhostDishNamesAsync(pending, ct);
            ghostEnrichment = await BuildGhostEnrichmentAsync(householdId, pending, ct);
        }

        var cellVm = new CellFragmentVm(date, slotId, slot?.Label ?? "", meals, WeekStart, Members, hardStanceWarning,
            pending, ghostDishNames, ghostEnrichment,
            IsHardConflict: ConflictCells.ContainsKey(key),
            UnfulfillableCellInfo: UnfulfillableCells.GetValueOrDefault(key));
        var railVm = new PlanRailVm(Insights, PendingCount, Oob: false);
        var barNavVm = BuildPlanBarNavVm(Oob: false);

        // plan-rail-reopen is rendered inside the _PlanRail partial — no separate render needed.
        var cellHtml = await RenderPartialToStringAsync("_MealCell", cellVm, ct);
        var railHtml = await RenderPartialToStringAsync("_PlanRail", railVm, ct);
        var barNavHtml = await RenderPartialToStringAsync("_PlanBarNav", barNavVm, ct);

        return new JsonResult(new { cellHtml, railHtml, barNavHtml, error = (string?)null });
    }

    /// <summary>
    /// Renders a Razor partial view to a string. Used by the island JSON endpoints to encode
    /// HTML fragments in JSON. The fragments are applied to the live DOM by the island.
    /// </summary>
    private async Task<string> RenderPartialToStringAsync<TModel>(
        string viewName, TModel model, CancellationToken ct = default)
    {
        var serviceProvider = HttpContext.RequestServices;
        var viewEngine = serviceProvider.GetRequiredService<ICompositeViewEngine>();
        var tempDataProvider = serviceProvider.GetRequiredService<ITempDataProvider>();

        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
            HttpContext,
            RouteData,
            PageContext.ActionDescriptor,
            ModelState);

        var viewResult = viewEngine.FindView(actionContext, viewName, isMainPage: false);
        if (!viewResult.Success)
            throw new InvalidOperationException($"Could not find partial view '{viewName}'.");

        var viewData = new ViewDataDictionary<TModel>(
            new EmptyModelMetadataProvider(), ModelState)
        {
            Model = model
        };
        var tempData = new TempDataDictionary(HttpContext, tempDataProvider);

        await using var writer = new System.IO.StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewData,
            tempData,
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    /// <summary>
    /// Builds DishSpec list from a JSON dish array (island JSON endpoint format).
    /// JSON carries a typed array of (kind, itemId, servings) objects, so per-dish
    /// servings can never be mis-mapped the way repeated form keys could collapse.
    /// </summary>
    private static List<DishSpec> BuildDishSpecsFromJson(List<DishJsonItem>? dishes)
    {
        if (dishes is null || dishes.Count == 0) return [];
        return dishes.Select(d => new DishSpec(
            d.Kind.Equals("recipe", StringComparison.OrdinalIgnoreCase) ? DishKind.Recipe : DishKind.Product,
            d.ItemId,
            Math.Max(1, d.Servings))).ToList();
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

        // Ghost-cell recipe names + rolled-up fulfillment/cost for any pending AI proposal.
        // _WeekGrid resolves names inline via ResolveRecipeName; the cell fragment must match, and
        // the roll-up reuses the shared best-effort helper (same transient-meal pattern as commits).
        IReadOnlyList<string>? ghostDishNames = null;
        MealFulfillmentVm? ghostEnrichment = null;
        if (pending is not null)
        {
            ghostDishNames = await ResolveGhostDishNamesAsync(pending, ct);
            ghostEnrichment = await BuildGhostEnrichmentAsync(householdId, pending, ct);
        }

        // Return the cell fragment plus out-of-band refreshes for both the insights rail and
        // the plan-bar nav projections. LoadWeekAsync (called above) has already recomputed
        // Model.Insights and Model.HasEmptyCells for the mutated plan, so both OOB fragments
        // are fresh. Routing every cell-targeted mutation through this single helper guarantees
        // the "recompute on EVERY change" invariant — no per-handler wiring to forget.
        var cellVm = new CellFragmentVm(date, slotId, slot?.Label ?? "", meals, WeekStart, Members, hardStanceWarning, pending, ghostDishNames, ghostEnrichment,
            IsHardConflict: ConflictCells.ContainsKey(key),
            UnfulfillableCellInfo: UnfulfillableCells.GetValueOrDefault(key));
        var railVm = new PlanRailVm(Insights, PendingCount, Oob: true,
            ConfirmedWeekCost: WeekTotalCost, ConfirmedCostIsPartial: WeekCostIsPartial,
            ProjectedWeekCost: ProjectedWeekCost, ProjectedCostIsPartial: ProjectedWeekCostIsPartial);
        var barNavVm = BuildPlanBarNavVm(Oob: true);
        return Partial("_CellWithRail", new CellWithRailVm(cellVm, railVm, barNavVm));
    }

    /// <summary>
    /// Builds the plan-bar nav view model from the currently-loaded week state.
    /// Must be called after LoadWeekAsync so WeekStart, HasEmptyCells, ProjectedWeekCost, etc. are set.
    /// </summary>
    private PlanBarNavVm BuildPlanBarNavVm(bool Oob) => new(
        WeekStart, PrevWeekStart, NextWeekStart, ThisWeekStart,
        WeekLabel, HasEmptyCells, WeekTotalCost, WeekCostIsPartial, Oob,
        ProjectedWeekCost: ProjectedWeekCost,
        ProjectedWeekCostIsPartial: ProjectedWeekCostIsPartial,
        PendingCount: PendingCount,
        WeekBudgetTarget: WeekBudgetTarget,
        ConfirmedOverBudget: WeekBudgetTarget.HasValue && WeekTotalCost > WeekBudgetTarget,
        ProjectedOverBudget: WeekBudgetTarget.HasValue && ProjectedWeekCost > WeekBudgetTarget);

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

    public static string CellKey(DateOnly date, MealSlotId slotId) => $"{date:yyyy-MM-dd}_{slotId.Value:N}";

    /// <summary>
    /// Used by _WeekGrid.cshtml to resolve a recipe name for ghost cell display.
    /// Returns from the in-memory bag cache when available (ADR-021); falls back to
    /// <see cref="IRecipeReadModel.GetByIdAsync"/> for any recipe not in the cache
    /// (e.g. if the grid renders before LoadWeekAsync is called, which should not happen in practice).
    /// </summary>
    public async Task<string> ResolveRecipeNameAsync(Guid recipeId)
    {
        if (_recipeNameCache.TryGetValue(recipeId, out var cachedName))
            return cachedName;

        // Fallback: recipe not in bag (should not occur for meals loaded this request).
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
        bool IsEditing,
        /// <summary>
        /// Server-computed initial rollup for the editor footer. Populated when opening an
        /// existing dish-based meal so the footer is meaningful on open (ADR-013 §4/§5 —
        /// the deleted client 'roll' getter used to compute this; now the server does it).
        /// Null when creating a new meal or when editing a note-only meal.
        /// </summary>
        int? InitialFulfillmentPercent = null,
        decimal? InitialTotalCost = null,
        bool InitialCostIsPartial = false);

    public sealed record EditorDishVm(
        DishKind Kind, Guid ItemId, string Name, int Servings, int Ordinal,
        int? FulfillmentPercent = null, decimal? CostPerServing = null, bool HasPhoto = false);

    public sealed record CellFragmentVm(
        DateOnly Date,
        MealSlotId SlotId,
        string SlotLabel,
        List<MealCellVm> Meals,
        DateOnly WeekStart,
        List<HouseholdMember> Members,
        string? HardStanceWarning = null,
        ProposedMeal? PendingProposal = null,
        IReadOnlyList<string>? GhostDishNames = null,
        MealFulfillmentVm? GhostEnrichment = null,
        /// <summary>
        /// True when this cell was flagged as an irreconcilable hard-stance conflict (C6) during
        /// the current generate pass. Renders the full actionable in-cell UI with dual CTAs:
        /// "Add a dish by hand" + "Adjust who's attending". Only populated during a generate response.
        /// </summary>
        bool IsHardConflict = false,
        /// <summary>
        /// Non-null when this cell was flagged as unfulfillable during the current generate pass —
        /// an attendee's Required tag has ZERO recipes in the full corpus. Carries the tag name for
        /// the actionable "Add a [tag] recipe" CTA. Only populated during a generate response.
        /// </summary>
        UnfulfillableCell? UnfulfillableCellInfo = null);

    /// <summary>
    /// View model for the advisory insights rail (P3-5). Rendered inline inside <c>_WeekGrid</c>
    /// on a full grid swap, and out-of-band (<paramref name="Oob"/> = true) alongside a cell
    /// fragment so the rail recomputes on EVERY plan change — see <c>_PlanRail.cshtml</c>.
    /// </summary>
    /// <param name="ConfirmedWeekCost">Confirmed week cost (plantry-5lp — shown in the rail toggle callout).</param>
    /// <param name="ConfirmedCostIsPartial">True when the confirmed figure is a partial estimate.</param>
    /// <param name="ProjectedWeekCost">Confirmed + pending ghost costs (plantry-5lp — shown in the rail toggle callout).</param>
    /// <param name="ProjectedCostIsPartial">True when the projected figure is a partial estimate.</param>
    public sealed record PlanRailVm(
        IReadOnlyList<InsightCallout> Insights,
        int PendingCount,
        bool Oob = false,
        decimal? ConfirmedWeekCost = null,
        bool ConfirmedCostIsPartial = false,
        decimal? ProjectedWeekCost = null,
        bool ProjectedCostIsPartial = false);

    /// <summary>
    /// Combines a single cell fragment with an out-of-band rail refresh and an out-of-band
    /// plan-bar nav refresh. Every cell-targeted plan mutation returns through this (via
    /// <c>CellFragmentAsync</c>) so both the rail and the plan-bar (HasEmptyCells, budget chip)
    /// recompute on EVERY change — no per-handler wiring to forget.
    /// </summary>
    public sealed record CellWithRailVm(CellFragmentVm Cell, PlanRailVm Rail, PlanBarNavVm BarNav);

    /// <summary>
    /// Combines the week grid with an out-of-band plan-bar nav refresh. Returned by any handler
    /// that swaps the full grid (Grid, Generate, AcceptAll, Discard, AcceptCell, RejectCell, Move)
    /// so the command bar (nav URLs, week label, This-week visibility, Auto-fill state, budget chip)
    /// updates atomically with the grid swap — eliminating the plan-bar staleness bug this ticket
    /// was filed to fix.
    /// </summary>
    public sealed record GridWithBarNavVm(IndexModel Grid, PlanBarNavVm BarNav);

    /// <summary>
    /// View model for the plan-bar nav partial (<c>_PlanBarNav.cshtml</c>). Carries the week-level
    /// derived projections that go stale after htmx grid swaps: nav button URLs, week label,
    /// This-week button visibility, Auto-fill disabled state, and the budget chip value.
    /// When <paramref name="Oob"/> is true the partial renders with hx-swap-oob so htmx replaces
    /// the live bar elements in place; when false it renders inline on first page load.
    /// </summary>
    /// <param name="ProjectedWeekCost">Confirmed + pending ghost costs (plantry-5lp).</param>
    /// <param name="ProjectedWeekCostIsPartial">True when the projected figure is a partial estimate.</param>
    /// <param name="PendingCount">Number of pending AI proposals (drives toggle rendering in the chip).</param>
    /// <param name="WeekBudgetTarget">Resolved weekly budget target (plantry-gx34 inline budget).</param>
    /// <param name="ConfirmedOverBudget">True when confirmed cost exceeds the budget target (plantry-gx34).</param>
    /// <param name="ProjectedOverBudget">True when projected cost exceeds the budget target (plantry-gx34).</param>
    public sealed record PlanBarNavVm(
        DateOnly WeekStart,
        DateOnly PrevWeekStart,
        DateOnly NextWeekStart,
        DateOnly ThisWeekStart,
        string WeekLabel,
        bool HasEmptyCells,
        decimal? WeekTotalCost,
        bool WeekCostIsPartial,
        bool Oob = false,
        decimal? ProjectedWeekCost = null,
        bool ProjectedWeekCostIsPartial = false,
        int PendingCount = 0,
        decimal? WeekBudgetTarget = null,
        bool ConfirmedOverBudget = false,
        bool ProjectedOverBudget = false);
    /// <summary>
    /// View model for the editor rollup footer (_EditorRollup.cshtml) — ADR-013 §4/§5.
    /// Returned by OnPostRollupJsonAsync; the island parses the JSON and injects the HTML.
    /// The rollup is a server-computed projection; no client-side formula exists.
    /// </summary>
    public sealed record EditorRollupVm(
        bool IsNote,
        bool HasDishes,
        int? FulfillmentPercent = null,
        decimal? TotalCost = null,
        bool CostIsPartial = false);
    public sealed record MealCardVm(MealCellVm Meal, string DateIso, MealSlotId SlotId, List<HouseholdMember> Members);
    /// <summary>
    /// Ghost cell (pending AI proposal). GhostEnrichment carries the server-computed
    /// rolled-up fulfillment % and estimated cost for display on the ghost cell (P3-6b, ADR-013 §4/§5).
    /// Like the editor rollup, this is a server projection — no client formula.
    /// </summary>
    public sealed record GhostCellVm(
        DateOnly Date,
        MealSlotId SlotId,
        string SlotLabel,
        ProposedMeal Proposal,
        IReadOnlyList<string> DishNames,
        MealFulfillmentVm? GhostEnrichment = null);

    // ── Island JSON endpoint input models (ADR-015 amendment) ─────────────────

    /// <summary>A single dish item in a JSON island request (kind + itemId + servings).</summary>
    public sealed class DishJsonItem
    {
        public string Kind { get; set; } = "recipe";
        public Guid ItemId { get; set; }
        public int Servings { get; set; } = 1;
    }

    /// <summary>JSON body for POST AssignJson — the island editor save action.</summary>
    public sealed class AssignJsonInput
    {
        public string Date { get; set; } = "";
        public Guid SlotId { get; set; }
        public string Mode { get; set; } = "dishes";
        public string? Note { get; set; }
        public List<DishJsonItem>? Dishes { get; set; }
        public List<Guid>? Att { get; set; }
        public bool AttendeesOverridden { get; set; }
        public Guid? MealId { get; set; }
    }

    /// <summary>JSON body for POST ClearJson — the island editor remove-meal action.</summary>
    public sealed class ClearJsonInput
    {
        public string Date { get; set; } = "";
        public Guid SlotId { get; set; }
        public Guid MealId { get; set; }
    }

    /// <summary>JSON body for POST RollupJson — the island editor rollup projection request.</summary>
    public sealed class RollupJsonInput
    {
        public string Mode { get; set; } = "dishes";
        public List<DishJsonItem>? Dishes { get; set; }
    }

    // IslandHydrationVm and IslandMemberVm are defined in MealPlanHydration.cs (plantry-eoj5 Phase A).
}
