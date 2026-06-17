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

namespace Plantry.Web.Pages.MealPlan;

[Authorize]
public sealed class IndexModel(
    IMealPlanRepository mealPlanRepo,
    IMealSlotConfigRepository slotConfigRepo,
    AssignMealService assignService,
    MoveMealService moveService,
    ShopForWeekService shopForWeekService,
    PlanFulfillmentService fulfillmentService,
    PlanCostingService costingService,
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

    public List<DayColumn> WeekDays { get; private set; } = [];
    public List<SlotRow> Slots { get; private set; } = [];
    public List<HouseholdMember> Members { get; private set; } = [];

    /// <summary>All planned meals for this week, keyed by "date_slotId".</summary>
    public Dictionary<string, List<MealCellVm>> MealsByCell { get; private set; } = [];

    /// <summary>Rolled-up week cost for the budget chip. Null when no pricing data available.</summary>
    public decimal? WeekTotalCost { get; private set; }

    /// <summary>True when any week cost was computed with partial pricing data.</summary>
    public bool WeekCostIsPartial { get; private set; }

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
                // Resolve dish names
                var dishes = new List<EditorDishVm>();
                foreach (var d in meal.PlannedDishes.OrderBy(d => d.Ordinal))
                {
                    if (d.RecipeId.HasValue)
                    {
                        var r = await recipeReader.GetByIdAsync(d.RecipeId.Value, ct);
                        dishes.Add(new EditorDishVm(DishKind.Recipe, d.RecipeId.Value, r?.Name ?? "Unknown recipe", d.Servings, d.Ordinal));
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
        CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var userId = await GetCurrentUserIdAsync(ct);
        var sid = MealSlotId.From(slotId);

        List<Guid>? overrideList = attendeesOverridden ? attendeesOverride ?? [] : null;

        string? hardStanceWarning = null;
        if (mode == "note")
        {
            if (string.IsNullOrWhiteSpace(note)) return BadRequest("Note is required.");
            var noteResult = await assignService.AssignNoteAsync(householdId, parsedDate, sid, note!, overrideList, userId, ct);
            hardStanceWarning = noteResult.HardStanceWarning;
        }
        else
        {
            // Build dishes from three index-aligned arrays (kind, itemId, servings) so that
            // servings are never mis-mapped when a meal mixes recipe and product dishes.
            var specs = BuildDishSpecs(dishKinds, dishItemIds, dishServings);
            if (specs.Count == 0) return BadRequest("At least one dish is required.");
            var dishResult = await assignService.AssignDishesAsync(householdId, parsedDate, sid, specs, overrideList, userId, ct);
            hardStanceWarning = dishResult.HardStanceWarning;
        }

        // Return the updated cell fragment, including any dietary warning so the UI can display it
        return await CellFragmentAsync(householdId, parsedDate, sid, hardStanceWarning, ct);
    }

    // ── Clear meal POST ──────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostClearAsync(
        string date, Guid slotId, CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        var sid = MealSlotId.From(slotId);
        await assignService.ClearMealAsync(householdId, parsedDate, sid, ct);

        return await CellFragmentAsync(householdId, parsedDate, sid, ct);
    }

    // ── Move meal POST ───────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostMoveAsync(
        string fromDate, Guid fromSlotId,
        string toDate, Guid toSlotId,
        CancellationToken ct = default)
    {
        if (!DateOnly.TryParse(fromDate, out var from) ||
            !DateOnly.TryParse(toDate, out var to))
            return BadRequest();

        var householdId = HouseholdId.From(tenant.HouseholdId ?? Guid.Empty);
        await moveService.MoveAsync(householdId, from, MealSlotId.From(fromSlotId), to, MealSlotId.From(toSlotId), ct);

        // Return the whole grid for the source week
        var weekStr = DomainMealPlan.NormalizeToMonday(from).ToString("yyyy-MM-dd");
        await LoadWeekAsync(weekStr, ct);
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
        var recipes = await recipeReader.SearchAsync(q, 8, ct);
        var products = await catalogReader.SearchAsync(q, 5, ct);
        return Partial("_DishSearch", new DishSearchVm(q, recipes, products));
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
            ? $"{WeekStart:MMM d} – {end:d}, {WeekStart:yyyy}"
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
        if (HasSlots)
        {
            var plan = await mealPlanRepo.FindByWeekAsync(householdId, WeekStart, ct);

            if (plan is not null)
            {
                // Compute week-level cost roll-up for the budget chip.
                var weekCost = await costingService.RollUpWeekAsync(plan, ct);
                WeekTotalCost = weekCost.Amount;
                WeekCostIsPartial = weekCost.Completeness == CostCompleteness.Partial;

                foreach (var meal in plan.PlannedMeals)
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
    }

    private async Task<IActionResult> CellFragmentAsync(HouseholdId householdId, DateOnly date, MealSlotId slotId, CancellationToken ct)
        => await CellFragmentAsync(householdId, date, slotId, null, ct);

    private async Task<IActionResult> CellFragmentAsync(HouseholdId householdId, DateOnly date, MealSlotId slotId, string? hardStanceWarning, CancellationToken ct)
    {
        await LoadWeekAsync(null, ct);
        var key = CellKey(date, slotId);
        var meals = MealsByCell.GetValueOrDefault(key) ?? [];
        var slot = Slots.FirstOrDefault(s => s.Id == slotId);

        return Partial("_MealCell", new CellFragmentVm(date, slotId, slot?.Label ?? "", meals, WeekStart, hardStanceWarning));
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

    // ── View models ───────────────────────────────────────────────────────────

    public sealed record DayColumn(string DayName, string DateLabel, string DateIso, bool IsToday);
    public sealed record SlotRow(MealSlotId Id, string Label, List<Guid> DefaultAttendees);

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

    public sealed record EditorDishVm(DishKind Kind, Guid ItemId, string Name, int Servings, int Ordinal);

    public sealed record EditorPageModel(MealEditorVm Vm, List<HouseholdMember> Members, MealSlot Slot);
    public sealed record CellFragmentVm(DateOnly Date, MealSlotId SlotId, string SlotLabel, List<MealCellVm> Meals, DateOnly WeekStart, string? HardStanceWarning = null);
    public sealed record DishSearchVm(string Query, IReadOnlyList<RecipeReadModel> Recipes, IReadOnlyList<MealPlanProductReadModel> Products);
    public sealed record MealCardVm(MealCellVm Meal, string DateIso, MealSlotId SlotId, List<HouseholdMember> Members);
}
