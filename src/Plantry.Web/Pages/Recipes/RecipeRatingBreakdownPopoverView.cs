using Plantry.Recipes.Application;

namespace Plantry.Web.Pages.Recipes;

/// <summary>
/// View model for the shared <c>_RecipeRatingBreakdown</c> partial (plantry-zlwp.4 critic FIX) — the
/// per-member rating popover content, extracted so the Details household summary and the Browse
/// gallery/grid rating pills all render the exact same "Household N.N avg" title + per-member row
/// list from one call site instead of three independent copies.
/// </summary>
/// <param name="PopoverId">
/// The <c>id</c> the popover content carries (and the trigger's <c>aria-describedby</c> points at) —
/// must be unique per rendered instance: fixed (<c>rd-rating-pop</c>) on Details where there is only
/// one, per-recipe (<c>rating-pop-card-{RecipeId}</c> / <c>rating-pop-grid-{RecipeId}</c>) on Browse
/// where a page renders many.
/// </param>
/// <param name="HouseholdAvg">
/// The household average shown in the popover title. Callers only render this partial when a rating
/// exists (Browse: <c>RatedCount &gt; 0</c>; Details: <c>ShowHouseholdLine</c>), so this is always a
/// real average, never a placeholder for "nobody has rated".
/// </param>
/// <param name="Breakdown">The per-member rows, "You" first (see <see cref="GetRecipeRatingBreakdownQuery"/> / <see cref="RecipeRatingBreakdown"/>).</param>
public sealed record RecipeRatingBreakdownPopoverView(
    string PopoverId,
    decimal HouseholdAvg,
    IReadOnlyList<RecipeRatingBreakdownRow> Breakdown);
