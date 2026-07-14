using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Web.Pages.Deals;
using Xunit;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// L1 unit tests for <see cref="DealReviewQueueBuilder.ResolveStep"/> (plantry-y142, DEFER from plantry-qu1y
/// Gate 10) — the step-resolution fold, the most bug-prone part of the queue builder and previously exercised
/// only indirectly through the 40 L4 <see cref="DealReviewPageTests"/>. ResolveStep reads nothing but the three
/// lists' <c>Count</c>, so the fixtures are trivial placeholder views; contents never matter here.
///
/// The contract: no request → the first step with work (entry). An explicit step with work → that step,
/// regardless of verb. An explicit step now empty → auto-advance to the first step with work on a POST
/// (<c>autoAdvance: true</c>, it just emptied under the user's action), else honour it on a GET jump so the
/// empty-state renders and a refresh stays put. The requested index is clamped into [1,3] first.
/// </summary>
public sealed class DealReviewQueueBuilderStepTests
{
    /// <summary>A list of <paramref name="count"/> placeholder views — only the Count is read by ResolveStep.</summary>
    private static IReadOnlyList<DealReviewView> Views(int count)
    {
        var today = new DateOnly(2026, 1, 1);
        return Enumerable.Range(0, count).Select(_ => new DealReviewView(
            DealId.New(), Guid.NewGuid(), "FreshCo", "Deal", "Brand", "Save $1", 4.99m, null,
            today.AddDays(-1), today.AddDays(6), MatchConfidence.High, "reason",
            Guid.NewGuid(), "Some Product", DealStatus.Pending, AutoMatched: false)).ToList();
    }

    private static readonly IReadOnlyList<DealReviewView> Empty = Views(0);

    // --- requested == null → first non-empty step (entry) ---

    [Fact(DisplayName = "No request, step 1 has work → Confirm (first non-empty)")]
    public void NullRequest_Step1NonEmpty_Confirm()
    {
        Assert.Equal(ReviewStep.Confirm, DealReviewQueueBuilder.ResolveStep(null, false, Views(2), Views(1), Views(1)));
    }

    [Fact(DisplayName = "No request, step 1 empty and step 2 has work → Judgement (first non-empty)")]
    public void NullRequest_Step2FirstNonEmpty_Judgement()
    {
        Assert.Equal(ReviewStep.Judgement, DealReviewQueueBuilder.ResolveStep(null, false, Empty, Views(3), Views(1)));
    }

    [Fact(DisplayName = "No request, only step 3 has work → Everything (first non-empty)")]
    public void NullRequest_Step3FirstNonEmpty_Everything()
    {
        Assert.Equal(ReviewStep.Everything, DealReviewQueueBuilder.ResolveStep(null, false, Empty, Empty, Views(2)));
    }

    [Fact(DisplayName = "No request, all three empty → Confirm (default)")]
    public void NullRequest_AllEmpty_DefaultsToConfirm()
    {
        Assert.Equal(ReviewStep.Confirm, DealReviewQueueBuilder.ResolveStep(null, false, Empty, Empty, Empty));
    }

    // --- explicit step that has work → that step, regardless of autoAdvance ---

    [Theory(DisplayName = "Explicit step with work → that step, whatever the verb")]
    [InlineData(1, ReviewStep.Confirm)]
    [InlineData(2, ReviewStep.Judgement)]
    [InlineData(3, ReviewStep.Everything)]
    public void ExplicitStepWithWork_ReturnsThatStep_RegardlessOfVerb(int requested, ReviewStep expected)
    {
        // All three steps have work, so the requested one always has work.
        Assert.Equal(expected, DealReviewQueueBuilder.ResolveStep(requested, autoAdvance: false, Views(1), Views(1), Views(1)));
        Assert.Equal(expected, DealReviewQueueBuilder.ResolveStep(requested, autoAdvance: true, Views(1), Views(1), Views(1)));
    }

    // --- explicit step now empty, autoAdvance: true (POST) → first non-empty step ---

    [Fact(DisplayName = "Explicit empty step, POST → advances to the first non-empty step")]
    public void ExplicitEmptyStep_PostAutoAdvances_ToFirstNonEmpty()
    {
        // Requested step 1 is empty; step 2 has work → advance to Judgement.
        Assert.Equal(ReviewStep.Judgement, DealReviewQueueBuilder.ResolveStep(1, autoAdvance: true, Empty, Views(2), Views(1)));
    }

    [Fact(DisplayName = "Explicit empty step, POST, later step empty too → advances past both to the first with work")]
    public void ExplicitEmptyStep_PostAutoAdvances_SkipsEmptyToNonEmpty()
    {
        // Requested step 2 empty, step 1 has work → advance to Confirm (first non-empty overall).
        Assert.Equal(ReviewStep.Confirm, DealReviewQueueBuilder.ResolveStep(2, autoAdvance: true, Views(1), Empty, Views(1)));
    }

    // --- explicit step now empty, autoAdvance: true, all empty → the requested step (first ?? req) ---

    [Fact(DisplayName = "Explicit empty step, POST, all empty → the requested step (fallback first ?? req)")]
    public void ExplicitEmptyStep_PostAllEmpty_FallsBackToRequested()
    {
        Assert.Equal(ReviewStep.Everything, DealReviewQueueBuilder.ResolveStep(3, autoAdvance: true, Empty, Empty, Empty));
    }

    // --- explicit step now empty, autoAdvance: false (GET jump) → the requested step ---

    [Theory(DisplayName = "Explicit empty step, GET jump → honours the requested step (empty-state; refresh idempotent)")]
    [InlineData(1, ReviewStep.Confirm)]
    [InlineData(2, ReviewStep.Judgement)]
    [InlineData(3, ReviewStep.Everything)]
    public void ExplicitEmptyStep_GetJump_HonoursRequested(int requested, ReviewStep expected)
    {
        // The requested step is empty but other steps have work; GET must still land on the requested step.
        var step1 = requested == 1 ? Empty : Views(1);
        var step2 = requested == 2 ? Empty : Views(1);
        var step3 = requested == 3 ? Empty : Views(1);
        Assert.Equal(expected, DealReviewQueueBuilder.ResolveStep(requested, autoAdvance: false, step1, step2, step3));
    }

    // --- clamping: requested outside [1,3] is clamped before resolution ---

    [Theory(DisplayName = "Requested index is clamped into [1,3] before resolution")]
    [InlineData(0, ReviewStep.Confirm)]   // 0 → 1
    [InlineData(-1, ReviewStep.Confirm)]  // negative → 1
    [InlineData(4, ReviewStep.Everything)] // 4 → 3
    [InlineData(99, ReviewStep.Everything)] // 99 → 3
    public void RequestedIndex_IsClampedInto_1To3(int requested, ReviewStep expected)
    {
        // All three steps have work, so the clamped step always has work and is returned directly.
        Assert.Equal(expected, DealReviewQueueBuilder.ResolveStep(requested, autoAdvance: false, Views(1), Views(1), Views(1)));
    }

    [Fact(DisplayName = "Clamped step drives auto-advance too: requested 0 (→1) empty, POST → advances to the first with work")]
    public void ClampedStep_PostAutoAdvances()
    {
        // 0 clamps to step 1, which is empty; step 3 has work → advance to Everything.
        Assert.Equal(ReviewStep.Everything, DealReviewQueueBuilder.ResolveStep(0, autoAdvance: true, Empty, Empty, Views(1)));
    }
}
