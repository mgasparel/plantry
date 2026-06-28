using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Domain;
using Plantry.Intake.Application;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Intake.Domain;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Today;

/// <summary>
/// A single review banner to surface on the Today page.
///
/// The <see cref="Kind"/> discriminator is the extension point: new banner types (e.g. "deal"
/// for Phase-5, plantry-bpw) add a new kind value and a corresponding icon/colour variant in
/// <c>_ReviewBannerStack.cshtml</c> — no structural change to this record or to <see cref="IndexModel"/>
/// is needed.
/// </summary>
/// <param name="Kind">Banner category — currently "intake"; Phase-5 will add "deal".</param>
/// <param name="SessionId">Intake session ID as a string, used as the Alpine dismiss key.</param>
/// <param name="Title">Primary banner text, e.g. "7 items from Whole Foods are ready to review".</param>
/// <param name="Sub">Secondary text, e.g. "Forwarded by email · 2 hours ago".</param>
/// <param name="ActionUrl">URL the Review button navigates to, e.g. "/Intake/Review/{id}".</param>
/// <param name="CreatedAt">When the session was created — used to format the relative timestamp.</param>
public sealed record ReviewBannerItem(
    string Kind,
    string SessionId,
    string Title,
    string Sub,
    string ActionUrl,
    DateTimeOffset CreatedAt);

/// <summary>
/// View model for a single meal slot in the Today planned-meals band (plantry-zp7).
/// Represents either a planned meal or an empty slot for the current date.
/// </summary>
/// <param name="SlotId">The meal slot's stable ID.</param>
/// <param name="SlotLabel">Display label, e.g. "Breakfast".</param>
/// <param name="IsPlanned">True when the slot has at least one planned meal today.</param>
/// <param name="RecipeId">Primary recipe id when the first (or only) dish is a recipe; null for note meals or product-only meals.</param>
/// <param name="RecipeName">Display name of the primary recipe; null when not a recipe meal.</param>
/// <param name="HasPhoto">True when the primary recipe has a stored photo.</param>
/// <param name="DishNames">All dish names in the meal (for display when there are multiple dishes).</param>
/// <param name="Note">Free-text note when the meal is note-based; null otherwise.</param>
/// <param name="CookTimeMinutes">Cook time for the primary recipe; null when unknown or no recipe.</param>
/// <param name="Servings">Servings from the first planned dish.</param>
/// <param name="EffectiveAttendees">Resolved attendee user IDs (override ?? slot default).</param>
/// <param name="IsFullyCookable">True when fulfillment pct == 100 (all ingredients in stock).</param>
/// <param name="MealId">The planned meal's ID (for linking to the planner).</param>
/// <param name="HasExpiringIngredients">True when any ingredient has stock expiring within 4 days ("Use soon" flag).</param>
public sealed record PlannedMealSlotVm(
    MealSlotId SlotId,
    string SlotLabel,
    bool IsPlanned,
    Guid? RecipeId,
    string? RecipeName,
    bool HasPhoto,
    IReadOnlyList<string> DishNames,
    string? Note,
    int? CookTimeMinutes,
    int Servings,
    IReadOnlyList<Guid> EffectiveAttendees,
    bool IsFullyCookable,
    Guid MealId,
    bool HasExpiringIngredients);

[Authorize]
public sealed class IndexModel(
    IHouseholdRepository households,
    IProductStockRepository stocks,
    IImportSessionRepository sessions,
    PendingReviewQuery pendingReview,
    InventoryQueryService inventoryQueries,
    IMealPlanRepository mealPlanRepo,
    IMealSlotConfigRepository slotConfigRepo,
    PlanFulfillmentService planFulfillmentService,
    IRecipeReadModel recipeReadModel,
    IRecipeRepository recipeRepo,
    IHouseholdMemberReader memberReader,
    IClock clock,
    ITenantContext tenant) : PageModel
{
    /// <summary>The greeting text, e.g. "Good morning, Rivera household."</summary>
    public string Greeting { get; private set; } = string.Empty;

    /// <summary>Household name for the date line, e.g. "Rivera household".</summary>
    public string HouseholdName { get; private set; } = string.Empty;

    /// <summary>Formatted date string for the date line, e.g. "Thursday, June 18".</summary>
    public string DateDisplay { get; private set; } = string.Empty;

    /// <summary>
    /// True when the household has no stock, no recipes, and no pending intake sessions —
    /// triggers the Welcome hero instead of empty widget placeholders.
    /// </summary>
    public bool IsColdStart { get; private set; }

    /// <summary>
    /// True when the household has no tracked stock — surfaces the Take Stock CTA so
    /// the user is guided to /pantry/take-stock to populate the pantry (J6).
    /// Recedes once any stock exists; independent of IsColdStart so the CTA is shown
    /// even when the household has recipes but an empty pantry.
    /// </summary>
    public bool ShowTakeStockCta { get; private set; }

    /// <summary>
    /// Products expiring within the next <see cref="InventoryQueryService.ExpiringSoonDays"/> days
    /// (or already expired), ordered soonest-first. Empty when <see cref="IsColdStart"/> is true
    /// or the household has no stock nearing expiry.
    /// </summary>
    public IReadOnlyList<ExpiringSoonItem> ExpiringSoon { get; private set; } = [];

    /// <summary>
    /// Today's planned meal slots (one per active slot, in ordinal order) for the
    /// Phase-3 meals band (SPEC Page 0 §0c, plantry-zp7). Each slot is either planned
    /// (with recipe/dish/note info + fulfillment hint) or empty (showing a "Plan a meal" affordance).
    /// Empty list when <see cref="IsColdStart"/> is true or the household has no active meal slots.
    /// </summary>
    public IReadOnlyList<PlannedMealSlotVm> PlannedMealsToday { get; private set; } = [];

    /// <summary>
    /// True when every active meal slot has a planned meal today — triggers the
    /// "Every meal's planned" summary card in the meals band.
    /// </summary>
    public bool AllMealsPlanned => PlannedMealsToday.Count > 0 && PlannedMealsToday.All(s => s.IsPlanned);

    /// <summary>
    /// Household members for attendee avatar rendering in the meals band.
    /// Loaded once during <see cref="OnGetAsync"/> and consumed by <c>_PlannedMealsBand.cshtml</c>.
    /// Uses the <c>pl-av</c> avatar pattern (plenish.css §attendees) — each avatar gets a stable
    /// colour slot by index mod 8 so the same member always renders in the same hue.
    /// </summary>
    public List<HouseholdMember> Members { get; private set; } = [];

    /// <summary>
    /// Intake sessions in <c>Ready</c> status for this household — each becomes a dismissible
    /// review banner on the Today page (SPEC Page 0 §0b, plantry-yb6). Ordered newest first.
    /// Empty when <see cref="IsColdStart"/> is true or there are no pending sessions.
    /// The <see cref="ReviewBannerItem.Kind"/> field is the extensibility hook: Phase-5 (plantry-bpw)
    /// adds deal-review banners as a new kind without restructuring this list or the partial.
    /// </summary>
    public IReadOnlyList<ReviewBannerItem> PendingReviewBanners { get; private set; } = [];

    /// <summary>
    /// True when the expiring-soon badge should render in the urgent tone (at least one item
    /// with 0 or 1 day remaining, including expired lots).
    /// </summary>
    public bool ExpiringUrgent => ExpiringSoon.Any(x => x.DaysLeft <= 1);

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        DateDisplay = now.LocalDateTime.ToString("dddd, MMMM d");

        HouseholdId? householdId = tenant.HouseholdId is { } hid
            ? HouseholdId.From(hid)
            : null;

        if (householdId is { } houseId)
        {
            var household = await households.FindAsync(houseId, ct);
            HouseholdName = household?.Name ?? string.Empty;

            Greeting = BuildGreeting(now.LocalDateTime.Hour, HouseholdName);

            var hasStock = await stocks.AnyForHouseholdAsync(houseId, ct);
            var hasRecipes = await recipeRepo.AnyForHouseholdAsync(houseId, ct);
            var hasPendingIntake = await sessions.HasPendingAsync(houseId, ct);

            IsColdStart = !hasStock && !hasRecipes && !hasPendingIntake;
            ShowTakeStockCta = !hasStock;

            if (!IsColdStart)
            {
                ExpiringSoon = await inventoryQueries.ExpiringSoonAsync(ct);
                Members = (await memberReader.ListMembersAsync(ct)).ToList();
                PlannedMealsToday = await LoadPlannedMealsTodayAsync(houseId, now, ct);
                PendingReviewBanners = await LoadReviewBannersAsync(houseId, ct);
            }
        }
        else
        {
            Greeting = BuildGreeting(now.LocalDateTime.Hour, string.Empty);
            IsColdStart = true;
            ShowTakeStockCta = true;
        }
    }

    /// <summary>
    /// Loads today's planned meal slots for the meals band (plantry-zp7).
    /// Reads the MealPlan for today's week from the repository, then for each active
    /// slot (in ordinal order) produces a <see cref="PlannedMealSlotVm"/>: either a
    /// populated slot (planned meal + P3-4 fulfillment roll-up) or an empty-slot sentinel.
    /// </summary>
    private async Task<IReadOnlyList<PlannedMealSlotVm>> LoadPlannedMealsTodayAsync(
        HouseholdId householdId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        // Load slot config — defines the active slot vocabulary for this household.
        var slotConfig = await slotConfigRepo.FindByHouseholdAsync(householdId, ct);
        if (slotConfig is null)
            return [];

        var activeSlots = slotConfig.Slots
            .Where(s => s.IsActive)
            .OrderBy(s => s.Ordinal)
            .ToList();

        if (activeSlots.Count == 0)
            return [];

        // Load this week's plan (null = no plan created yet → all slots are empty).
        // Use fully-qualified name: Plantry.Web.Pages.MealPlan is a conflicting namespace.
        var weekStart = Plantry.MealPlanning.Domain.MealPlan.NormalizeToMonday(today);
        var plan = await mealPlanRepo.FindByWeekAsync(householdId, weekStart, ct);

        var result = new List<PlannedMealSlotVm>(activeSlots.Count);

        foreach (var slot in activeSlots)
        {
            // Get the first planned meal in this cell (by ordinal) — the band shows one per slot.
            var mealsInCell = plan?.MealsInCell(today, slot.Id)
                .OrderBy(m => m.Ordinal)
                .ToList() ?? [];

            var meal = mealsInCell.FirstOrDefault();

            if (meal is null)
            {
                // Empty slot — no meal planned yet.
                result.Add(new PlannedMealSlotVm(
                    SlotId: slot.Id,
                    SlotLabel: slot.Label,
                    IsPlanned: false,
                    RecipeId: null,
                    RecipeName: null,
                    HasPhoto: false,
                    DishNames: [],
                    Note: null,
                    CookTimeMinutes: null,
                    Servings: 0,
                    EffectiveAttendees: slot.DefaultAttendees,
                    IsFullyCookable: false,
                    MealId: Guid.Empty,
                    HasExpiringIngredients: false));
                continue;
            }

            // Compute enrichment (P3-4 roll-up) for dish-based meals.
            MealFulfillment fulfillment = MealFulfillment.None;
            if (meal.Note is null && meal.PlannedDishes.Count > 0)
            {
                fulfillment = await planFulfillmentService.RollUpMealAsync(meal, today, ct);
            }

            // Resolve dish display information.
            Guid? primaryRecipeId = null;
            string? primaryRecipeName = null;
            bool hasPhoto = false;
            int? cookTimeMinutes = null;
            int servings = 0;
            var dishNames = new List<string>();

            if (meal.Note is not null)
            {
                // Note-based meal: no recipe, just display the note text.
                dishNames.Add(meal.Note);
                servings = 1;
            }
            else
            {
                var orderedDishes = meal.PlannedDishes.OrderBy(d => d.Ordinal).ToList();
                servings = orderedDishes.FirstOrDefault()?.Servings ?? 0;

                // Build dish name list from dish recipe IDs (best-effort; null recipe = "Unknown recipe").
                // Primary recipe = first recipe dish (by ordinal).
                foreach (var dish in orderedDishes)
                {
                    if (dish.RecipeId.HasValue)
                    {
                        // Use IRecipeReadModel (MealPlanning ACL port) for name + HasPhoto — it's the
                        // established cross-context seam for MealPlanning→Recipes lookups.
                        var recipeModel = await recipeReadModel.GetByIdAsync(dish.RecipeId.Value, ct);
                        var name = recipeModel?.Name ?? "Unknown recipe";
                        dishNames.Add(name);

                        if (!primaryRecipeId.HasValue)
                        {
                            primaryRecipeId = dish.RecipeId.Value;
                            primaryRecipeName = name;
                            hasPhoto = recipeModel?.HasPhoto ?? false;

                            // CookTimeMinutes is not in IRecipeReadModel; load from the Recipes domain
                            // repository (composition root has full access — ADR-021 §3).
                            if (recipeModel is not null)
                            {
                                var recipeEntity = await recipeRepo.GetByIdAsync(
                                    RecipeId.From(dish.RecipeId.Value), ct);
                                cookTimeMinutes = recipeEntity?.CookTimeMinutes;
                            }
                        }
                    }
                    else if (dish.ProductId.HasValue)
                    {
                        // Product-dish: display a generic label (catalog name resolution is expensive;
                        // the meal plan editor already resolved these — not re-loaded here).
                        dishNames.Add("Product dish");
                    }
                }
            }

            // Effective attendees: override ?? slot default.
            var effectiveAttendees = (IReadOnlyList<Guid>)(meal.AttendeesOverride ?? slot.DefaultAttendees);

            result.Add(new PlannedMealSlotVm(
                SlotId: slot.Id,
                SlotLabel: slot.Label,
                IsPlanned: true,
                RecipeId: primaryRecipeId,
                RecipeName: primaryRecipeName,
                HasPhoto: hasPhoto,
                DishNames: dishNames,
                Note: meal.Note,
                CookTimeMinutes: cookTimeMinutes,
                Servings: servings,
                EffectiveAttendees: effectiveAttendees,
                IsFullyCookable: fulfillment.FulfillmentPercent == 100,
                MealId: meal.Id.Value,
                HasExpiringIngredients: fulfillment.HasExpiringIngredients));
        }

        return result;
    }

    /// <summary>
    /// Loads ready-to-review intake sessions and projects them to <see cref="ReviewBannerItem"/>
    /// view models for the Today banner stack. Each banner links to the session's review form.
    /// Returns an empty list when there are no pending sessions.
    /// </summary>
    private async Task<IReadOnlyList<ReviewBannerItem>> LoadReviewBannersAsync(
        HouseholdId householdId, CancellationToken ct)
    {
        var rows = await pendingReview.ExecuteAsync(householdId, ct);
        var now = clock.UtcNow;

        return rows.Select(r =>
        {
            var storePart = string.IsNullOrWhiteSpace(r.Store) ? "your receipt" : r.Store;
            var itemWord = r.ItemCount == 1 ? "item" : "items";
            var title = $"{r.ItemCount} {itemWord} from {storePart} {(r.ItemCount == 1 ? "is" : "are")} ready to review";
            var sub = BuildBannerSub(r, now);
            return new ReviewBannerItem(
                Kind: "intake",
                SessionId: r.Id.Value.ToString(),
                Title: title,
                Sub: sub,
                ActionUrl: $"/Intake/Review/{r.Id.Value}",
                CreatedAt: r.CreatedAt);
        }).ToList();
    }

    /// <summary>Builds the banner subtitle: source type + relative age.</summary>
    internal static string BuildBannerSub(PendingReviewRow row, DateTimeOffset now)
    {
        var age = now - row.CreatedAt;
        string relTime;
        if (age.TotalMinutes < 1)
            relTime = "just now";
        else if (age.TotalMinutes < 60)
            relTime = $"{(int)age.TotalMinutes} minute{((int)age.TotalMinutes == 1 ? "" : "s")} ago";
        else if (age.TotalHours < 24)
            relTime = $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago";
        else
            relTime = $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";

        return $"Forwarded by email · {relTime}";
    }

    /// <summary>Returns a greeting appropriate to the local hour.</summary>
    internal static string BuildGreeting(int hour, string householdName)
    {
        var salutation = hour switch
        {
            >= 5 and < 12  => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            >= 17 and < 21 => "Good evening",
            _              => "Good night",
        };

        return string.IsNullOrWhiteSpace(householdName)
            ? $"{salutation}."
            : $"{salutation}, {householdName}.";
    }
}
