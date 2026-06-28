using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Domain;
using Plantry.Intake.Application;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Intake.Domain;
using Plantry.Recipes.Application;
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

[Authorize]
public sealed class IndexModel(
    IHouseholdRepository households,
    IProductStockRepository stocks,
    IRecipeRepository recipes,
    IImportSessionRepository sessions,
    PendingReviewQuery pendingReview,
    InventoryQueryService inventoryQueries,
    BrowseRecipesQuery browseRecipes,
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
    /// Up to 3 cook-now recipe picks for the meals band (SPEC Page 0 §0c, plantry-81g).
    /// Sorted by expiring-ingredient first (use-it-up angle), then by fulfillment percentage
    /// descending. Empty when <see cref="IsColdStart"/> is true or the household has no recipes.
    /// Null when the query has not yet been executed (before <see cref="OnGetAsync"/> completes).
    /// </summary>
    public IReadOnlyList<RecipeBrowseRow> CookNowPicks { get; private set; } = [];

    /// <summary>
    /// Intake sessions in <c>Ready</c> status for this household — each becomes a dismissible
    /// review banner on the Today page (SPEC Page 0 §0b, plantry-yb6). Ordered newest first.
    /// Empty when <see cref="IsColdStart"/> is true or there are no pending sessions.
    /// The <see cref="ReviewBannerItem.Kind"/> field is the extensibility hook: Phase-5 (plantry-bpw)
    /// adds deal-review banners as a new kind without restructuring this list or the partial.
    /// </summary>
    public IReadOnlyList<ReviewBannerItem> PendingReviewBanners { get; private set; } = [];

    /// <summary>Number of cook-now picks to display on the Today page.</summary>
    internal const int CookNowPickCount = 3;

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
            var hasRecipes = await recipes.AnyForHouseholdAsync(houseId, ct);
            var hasPendingIntake = await sessions.HasPendingAsync(houseId, ct);

            IsColdStart = !hasStock && !hasRecipes && !hasPendingIntake;
            ShowTakeStockCta = !hasStock;

            if (!IsColdStart)
            {
                ExpiringSoon = await inventoryQueries.ExpiringSoonAsync(ct);
                CookNowPicks = await LoadCookNowPicksAsync(ct);
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
    /// Loads the cook-now recipe picks for the Today meals band.
    /// Runs the full <see cref="BrowseRecipesQuery"/> (all recipes, fulfillment sort), then picks
    /// the top <see cref="CookNowPickCount"/> rows sorted by:
    ///   1. HasIngredientExpiringSoon descending (use-it-up angle — expiring picks first)
    ///   2. FulfillmentPct descending (most cookable first within each tier)
    /// </summary>
    private async Task<IReadOnlyList<RecipeBrowseRow>> LoadCookNowPicksAsync(CancellationToken ct)
    {
        var result = await browseRecipes.ExecuteAsync(new BrowseRecipesFilter(), ct);
        return SelectCookNowPicks(result.Rows, CookNowPickCount);
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

    /// <summary>
    /// Applies the cook-now pick selection logic: expiring-ingredient recipes first (use-it-up),
    /// then fulfillment percentage descending, capped at <paramref name="maxPicks"/>.
    /// Extracted as a static helper for deterministic unit testing without database access.
    /// </summary>
    internal static IReadOnlyList<RecipeBrowseRow> SelectCookNowPicks(
        IReadOnlyList<RecipeBrowseRow> rows, int maxPicks = CookNowPickCount) =>
        rows
            .OrderByDescending(r => r.HasIngredientExpiringSoon)
            .ThenByDescending(r => r.FulfillmentPct)
            .Take(maxPicks)
            .ToList();

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
