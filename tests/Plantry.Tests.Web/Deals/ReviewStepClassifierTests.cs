using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Web.Pages.Deals;
using Xunit;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// L4 unit tests for <see cref="ReviewStepClassifier"/> (q9zr.13) — the pure partition of a flyer's pending
/// deals into the three step views. The load-bearing property is that it is a TOTAL partition over the demoted
/// set: every deal maps to exactly one step, so the stepper counts always sum to the pending total and nothing
/// is double-counted or stranded.
/// </summary>
public sealed class ReviewStepClassifierTests
{
    private static DealReviewView View(
        MatchConfidence confidence, Guid? suggested, decimal price = 4.99m, string name = "Deal")
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new DealReviewView(
            DealId.New(), Guid.NewGuid(), "FreshCo", name, "Brand", "Save $1", price, null,
            today.AddDays(-1), today.AddDays(6), confidence, "reason",
            suggested, suggested is null ? null : "Some Product", DealStatus.Pending, AutoMatched: false);
    }

    private static readonly HashSet<Guid> NoneDemoted = [];

    [Fact(DisplayName = "A confirmable High (has a suggestion, not noise, not demoted) → step 1")]
    public void Confirmable_High_Is_Step1()
    {
        var d = View(MatchConfidence.High, Guid.NewGuid());
        Assert.Equal(ReviewStep.Confirm, ReviewStepClassifier.StepOf(d, NoneDemoted));
    }

    [Fact(DisplayName = "A $0.00 High noise row → step 2 (never step 1, keeping the checklist == ConfirmAll-eligible)")]
    public void Noise_High_Is_Step2()
    {
        var d = View(MatchConfidence.High, Guid.NewGuid(), price: 0m);
        Assert.False(ReviewStepClassifier.IsConfirmableHigh(d));
        Assert.Equal(ReviewStep.Judgement, ReviewStepClassifier.StepOf(d, NoneDemoted));
    }

    [Fact(DisplayName = "A High with no live suggestion → step 2 (not confirmable)")]
    public void Suggestionless_High_Is_Step2()
    {
        var d = View(MatchConfidence.High, suggested: null);
        Assert.Equal(ReviewStep.Judgement, ReviewStepClassifier.StepOf(d, NoneDemoted));
    }

    [Fact(DisplayName = "A Low → step 2; a None → step 3")]
    public void Low_Is_Step2_None_Is_Step3()
    {
        Assert.Equal(ReviewStep.Judgement, ReviewStepClassifier.StepOf(View(MatchConfidence.Low, Guid.NewGuid()), NoneDemoted));
        Assert.Equal(ReviewStep.Everything, ReviewStepClassifier.StepOf(View(MatchConfidence.None, null), NoneDemoted));
    }

    [Fact(DisplayName = "A demoted deal → step 2, whatever its tier (unchecking a High moves it here)")]
    public void Demoted_Is_Step2()
    {
        var d = View(MatchConfidence.High, Guid.NewGuid());
        var demoted = new HashSet<Guid> { d.DealId.Value };
        Assert.Equal(ReviewStep.Judgement, ReviewStepClassifier.StepOf(d, demoted));
    }

    [Fact(DisplayName = "The classifier is a total partition: step counts sum to the pending total, no deal counted twice")]
    public void Is_A_Total_Partition()
    {
        var confirmable = View(MatchConfidence.High, Guid.NewGuid(), name: "H1");
        var noiseHigh = View(MatchConfidence.High, Guid.NewGuid(), price: 0m, name: "H2");
        var low = View(MatchConfidence.Low, Guid.NewGuid(), name: "L1");
        var demotedHigh = View(MatchConfidence.High, Guid.NewGuid(), name: "H3");
        var none1 = View(MatchConfidence.None, null, name: "N1");
        var none2 = View(MatchConfidence.None, null, name: "N2");
        var all = new[] { confirmable, noiseHigh, low, demotedHigh, none1, none2 };
        var demoted = new HashSet<Guid> { demotedHigh.DealId.Value };

        var step1 = all.Count(d => ReviewStepClassifier.StepOf(d, demoted) == ReviewStep.Confirm);
        var step2 = all.Count(d => ReviewStepClassifier.StepOf(d, demoted) == ReviewStep.Judgement);
        var step3 = all.Count(d => ReviewStepClassifier.StepOf(d, demoted) == ReviewStep.Everything);

        Assert.Equal(1, step1);                    // the confirmable High only
        Assert.Equal(3, step2);                    // noise High + Low + demoted High
        Assert.Equal(2, step3);                    // the two None
        Assert.Equal(all.Length, step1 + step2 + step3);  // total partition — nothing stranded, nothing doubled
    }
}
