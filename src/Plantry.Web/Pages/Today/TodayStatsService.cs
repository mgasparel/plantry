using Plantry.Pantry.Application;
using Plantry.Planning.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Pages.Today;

/// <summary>
/// One streak chip on the Today stats widget (plantry-h9z9) — <see cref="Icon"/> is a decorative emoji
/// glyph (mirrors the stats-page-prototype.html injection demo's 🔥/🥫 usage), <see cref="Value"/> the bold
/// number/phrase, <see cref="Label"/> the trailing description. Rendered via the existing <c>.chip-stat</c>
/// primitive (no new CSS) — <c>&lt;span class="chip-stat"&gt;@Icon &lt;b&gt;@Value&lt;/b&gt; @Label&lt;/span&gt;</c>.
/// </summary>
public sealed record TodayStreakChip(string Icon, string Value, string Label);

/// <summary>
/// View model for the Today stats widget (plantry-h9z9, stats-page-prototype.html appendix "Today" injection
/// point): one rotating "did you know" fact plus the household's persistent streak chips. Unlike
/// <c>StatsPanelViewModel</c> on the product detail page, this is never null — <see cref="RotatingFactText"/>
/// always resolves to something (the household-tenure fallback fact has no data precondition), so the widget
/// always renders once the household is past cold start; only <see cref="StreakChips"/> can be empty.
/// </summary>
public sealed record TodayStatsVm(string RotatingFactText, IReadOnlyList<TodayStreakChip> StreakChips);

/// <summary>
/// Builds <see cref="TodayStatsVm"/> for the Today page (plantry-h9z9) — a single-purpose, self-contained
/// read model kept out of <see cref="IndexModel"/> per the ticket's explicit steer ("Today is a hotspot page
/// … prefer a self-contained partial + read model over growing the page model"). Composes three independent,
/// cheap signals:
/// <list type="bullet">
/// <item>Household-wide waste count over a trailing window (<see cref="IWasteJournalReader.CountDiscardedSinceAsync"/>
/// — one indexed <c>COUNT</c>).</item>
/// <item>Consecutive weekly planning streak (<see cref="MealPlanStreakQuery"/> — one scalar-only query
/// returning the household's planned week-starts, walked in memory).</item>
/// <item>Household tenure (<see cref="Plantry.Identity.Domain.Household.CreatedAt"/>, already loaded by
/// <see cref="IndexModel.OnGetAsync"/> for the greeting — zero marginal cost).</item>
/// </list>
/// Each independently degrades: the waste fact and the tenure fact are always available (a zero count or a
/// same-day household still produces a valid sentence), so the rotation can never land on "nothing to show."
/// </summary>
public sealed class TodayStatsService(
    IWasteJournalReader wasteJournal,
    MealPlanStreakQuery streakQuery,
    IClock clock)
{
    /// <summary>Trailing window, in days, the rotating "waste trend" fact counts Discarded events over.</summary>
    internal const int WasteTrendWindowDays = 30;

    public async Task<TodayStatsVm> BuildAsync(
        HouseholdId householdId,
        DateTimeOffset householdCreatedAt,
        DateOnly today,
        CancellationToken ct = default)
    {
        var streakWeeks = await streakQuery.ExecuteAsync(householdId, today, ct);
        var lastDiscardAt = await wasteJournal.MostRecentDiscardAsync(ct);
        var wasteCount = await wasteJournal.CountDiscardedSinceAsync(
            clock.UtcNow.AddDays(-WasteTrendWindowDays), ct);

        var chips = new List<TodayStreakChip>();
        if (streakWeeks > 0)
            chips.Add(new TodayStreakChip("🔥", $"{streakWeeks}-week", "planning streak"));
        if (lastDiscardAt is { } discardAt)
        {
            var daysSince = Math.Max(0, today.DayNumber - clock.ToLocalDate(discardAt).DayNumber);
            chips.Add(new TodayStreakChip(
                "🥫", daysSince.ToString(), $"day{(daysSince == 1 ? "" : "s")} since anything expired"));
        }

        var tenureDays = Math.Max(0, today.DayNumber - clock.ToLocalDate(householdCreatedAt).DayNumber);
        var rotatingFact = BuildRotatingFact(today, streakWeeks, wasteCount, tenureDays);

        return new TodayStatsVm(rotatingFact, chips);
    }

    /// <summary>
    /// Picks one "did you know" sentence deterministically by calendar day (<see cref="DateOnly.DayNumber"/>
    /// — a day-since-year-1 count, so the rotation never repeats a candidate two days running by coincidence
    /// of month/year rollover) from a fixed three-candidate pool: waste trend, planning streak, household
    /// tenure. Starts at <c>today.DayNumber % candidates.Count</c> and walks forward (wrapping) until a
    /// candidate returns non-null — the planning-streak candidate is the only one that can decline (a
    /// zero-week streak has nothing to say); waste trend and tenure are unconditional, so the walk always
    /// terminates within the pool and this method never returns without a real fact.
    /// </summary>
    internal static string BuildRotatingFact(DateOnly today, int streakWeeks, int wasteCountLast30Days, int tenureDays)
    {
        var candidates = new Func<string?>[]
        {
            () => wasteCountLast30Days == 0
                ? $"Nothing's gone to waste in the last {WasteTrendWindowDays} days — keep it up."
                : $"{wasteCountLast30Days} item{(wasteCountLast30Days == 1 ? "" : "s")} marked wasted in the last {WasteTrendWindowDays} days.",
            () => streakWeeks > 0
                ? $"You've planned meals {streakWeeks} week{(streakWeeks == 1 ? "" : "s")} in a row."
                : null,
            () => $"You've been tracking your pantry with Plantry for {tenureDays} day{(tenureDays == 1 ? "" : "s")}.",
        };

        var start = today.DayNumber % candidates.Length;
        for (var offset = 0; offset < candidates.Length; offset++)
        {
            var text = candidates[(start + offset) % candidates.Length]();
            if (text is not null)
                return text;
        }

        // Unreachable: candidates[0] and candidates[2] are unconditional, so the loop above always
        // returns before falling through — kept only so the method has a total return type.
        return "Welcome to Plantry.";
    }
}
