using Plantry.Web.Dev;

namespace Plantry.Tests.Web.Dev;

/// <summary>
/// Guards the flyer/deal seed shaping for plantry-q9zr.14 — the deterministic replay of the checked-in
/// real-ingest export (superstore-flyer-2026-07.json) into a current-week and a prior-week flyer. These are
/// pure/static assertions over the embedded fixture, the IClock-relative window math, and the prior-week
/// status plan (no DB or Aspire stack): the live end-to-end seed is proven by the epic's isolated-mode
/// verification recipe. The load-bearing assertion is <see cref="PriorWeek_Pending_Deals_Are_Excluded_By_DD14"/>,
/// which proves the seed's expired-Pending tripwire never surfaces in the review queue.
/// </summary>
public sealed class FakeDataSeederDealsTests
{
    private static readonly DateOnly Today = new(2026, 7, 7);

    // ── Fixture shape ────────────────────────────────────────────────────────────────

    [Fact]
    public void Fixture_Loads_The_Full_Real_Deal_Set()
    {
        var fixture = FakeDataSeeder.LoadFixture();

        Assert.Equal("Real Canadian Superstore", fixture.Store);
        Assert.Equal("8006782", fixture.Flyer.ExternalId);
        Assert.Equal(451, fixture.Deals.Count);
        // The raw payload survives verbatim for the ACL quarantine (DD6) — a non-empty jsonb array.
        Assert.Equal(System.Text.Json.JsonValueKind.Array, fixture.Flyer.RawFlyer.ValueKind);
        Assert.Equal(451, fixture.Flyer.RawFlyer.GetArrayLength());
    }

    [Fact]
    public void Fixture_Preserves_The_Real_Three_Tier_Confidence_Mix()
    {
        var deals = FakeDataSeeder.LoadFixture().Deals;

        Assert.Equal(401, deals.Count(d => d.Confidence == "none"));
        Assert.Equal(29, deals.Count(d => d.Confidence == "low"));
        Assert.Equal(21, deals.Count(d => d.Confidence == "high"));
    }

    [Fact]
    public void Fixture_Has_A_Suggestion_On_Exactly_The_Matched_Rows()
    {
        var deals = FakeDataSeeder.LoadFixture().Deals;

        // 50 rows carry a resolved post-match product name (the 21 high + 29 low tiers); the 401 "none"
        // noise rows carry no suggestion — the exact split the seeder replays without ever touching the AI.
        Assert.Equal(50, deals.Count(d => d.SuggestedProductName is not null));
        Assert.All(deals.Where(d => d.Confidence == "none"), d => Assert.Null(d.SuggestedProductName));
    }

    // ── IClock-relative window math ────────────────────────────────────────────────────

    [Fact]
    public void Current_Week_Window_Is_Active_And_Straddles_Today()
    {
        var (from, to) = FakeDataSeeder.CurrentWeekWindow(Today);

        Assert.Equal(Today.AddDays(-2), from);
        Assert.Equal(Today.AddDays(5), to);
        // Active: the review queue (DD14: Pending ∧ today ≤ valid_to) keeps every current-week Pending deal.
        Assert.True(Today <= to);
    }

    [Fact]
    public void Prior_Week_Window_Is_Expired_And_Butts_Against_The_Current_Week()
    {
        var (priorFrom, priorTo) = FakeDataSeeder.PriorWeekWindow(Today);
        var (curFrom, _) = FakeDataSeeder.CurrentWeekWindow(Today);

        Assert.Equal(Today.AddDays(-9), priorFrom);
        Assert.Equal(Today.AddDays(-2), priorTo);
        // Expired two days ago and contiguous with the current week (prior valid_to == current valid_from).
        Assert.True(priorTo < Today);
        Assert.Equal(curFrom, priorTo);
    }

    /// <summary>
    /// The load-bearing tripwire assertion (DD14). A prior-week deal left Pending is expired — its valid_to is
    /// two days in the past — so the review-queue predicate the page applies (<c>Pending ∧ today ≤ valid_to</c>)
    /// excludes it, while every current-week Pending deal (valid_to five days ahead) is kept. Proving this over
    /// the seeder's own window math is what keeps the seed a permanent live tripwire for the queue filter.
    /// </summary>
    [Fact]
    public void PriorWeek_Pending_Deals_Are_Excluded_By_DD14()
    {
        var (_, curTo) = FakeDataSeeder.CurrentWeekWindow(Today);
        var (_, priorTo) = FakeDataSeeder.PriorWeekWindow(Today);

        // DD14: a Pending deal shows in the review queue iff today ≤ its valid_to.
        static bool InReviewQueue(DateOnly today, DateOnly validTo) => today <= validTo;

        Assert.True(InReviewQueue(Today, curTo));    // current-week Pending → visible
        Assert.False(InReviewQueue(Today, priorTo)); // prior-week Pending → filtered out (expired)
    }

    // ── Prior-week status plan (driven through the real domain verbs) ──────────────────

    [Fact]
    public void PriorWeek_Confirms_The_Majority_Of_Resolvable_Deals()
    {
        var deals = FakeDataSeeder.LoadFixture().Deals;
        var hasSuggestion = deals.Select(d => d.SuggestedProductName is not null).ToList();

        var actions = FakeDataSeeder.PlanPriorWeekActions(hasSuggestion);

        var suggestionCount = hasSuggestion.Count(x => x);
        var confirmed = actions.Count(a => a == FakeDataSeeder.PriorWeekAction.Confirm);

        // Every Confirm must map to a suggestion-bearing deal (Confirm needs a resolved product), and the
        // confirmed count is the strict majority of resolvable deals — the "majority Confirmed" seed intent.
        Assert.All(
            Enumerable.Range(0, actions.Count).Where(i => actions[i] == FakeDataSeeder.PriorWeekAction.Confirm),
            i => Assert.True(hasSuggestion[i]));
        Assert.True(confirmed > suggestionCount / 2, $"expected majority of {suggestionCount} resolvable confirmed, got {confirmed}");
    }

    [Fact]
    public void PriorWeek_Rejects_A_Few_And_Leaves_A_Handful_Pending()
    {
        var deals = FakeDataSeeder.LoadFixture().Deals;
        var hasSuggestion = deals.Select(d => d.SuggestedProductName is not null).ToList();

        var actions = FakeDataSeeder.PlanPriorWeekActions(hasSuggestion);

        var rejected = actions.Count(a => a == FakeDataSeeder.PriorWeekAction.Reject);
        var pending = actions.Count(a => a == FakeDataSeeder.PriorWeekAction.Pending);

        // A few Rejected (all unmatched noise — a Reject never needs a product), and a non-empty handful left
        // Pending (the DD14 tripwire). Every action covers exactly one deal.
        Assert.Equal(6, rejected);
        Assert.All(
            Enumerable.Range(0, actions.Count).Where(i => actions[i] == FakeDataSeeder.PriorWeekAction.Reject),
            i => Assert.False(hasSuggestion[i]));
        Assert.True(pending > 0);
        Assert.Equal(deals.Count, actions.Count);
        // Some suggestion-bearing deals are deliberately left expired-Pending too, so the tripwire is not
        // only unmatched noise.
        Assert.Contains(
            Enumerable.Range(0, actions.Count),
            i => actions[i] == FakeDataSeeder.PriorWeekAction.Pending && hasSuggestion[i]);
    }
}
