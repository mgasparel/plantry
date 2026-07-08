using Plantry.Deals.Application;
using Plantry.Deals.Domain;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// The three jumpable tier-step VIEWS of the guided review flow (q9zr.13, adopted design B / focus.html).
/// A step is a view over the active flyer's pending deals — not a wizard position — so a deal always belongs
/// to exactly one step and the counts sum to the flyer's pending total.
/// </summary>
public enum ReviewStep
{
    /// <summary>Confirm the sure things — the pre-checked batch checklist of confirmable High-confidence deals.</summary>
    Confirm = 1,

    /// <summary>Judgement calls — pending Low deals plus any High demoted out of step 1 (and non-confirmable Highs).</summary>
    Judgement = 2,

    /// <summary>Everything else — the pending "Not in your catalog" (None) deals.</summary>
    Everything = 3,
}

/// <summary>
/// Assigns each pending deal to the single step that OWNS it (q9zr.13 scope 2 — tier ownership so counts never
/// double-count and nothing is stranded). Given the household's demoted-deal set (presentation state from
/// <see cref="IReviewFlowStateStore"/>), this is a total partition of the active flyer's pending deals:
/// <list type="bullet">
///   <item>a demoted deal → <see cref="ReviewStep.Judgement"/> (unchecking a High in step 1 moves it here);</item>
///   <item>a None-tier deal → <see cref="ReviewStep.Everything"/>;</item>
///   <item>a confirmable High (has a live suggestion and is not flyer-noise) → <see cref="ReviewStep.Confirm"/>;</item>
///   <item>everything else — a Low, or a non-confirmable High (a $0.00 noise row or a High with no suggestion)
///     → <see cref="ReviewStep.Judgement"/>. Routing the noise High here (rather than the step-1 checklist) keeps
///     the step-1 count identical to the ConfirmAll-eligible set, so "Confirm N matches" is always honest.</item>
/// </list>
/// </summary>
public static class ReviewStepClassifier
{
    /// <summary>True when a High deal is one the step-1 checklist may confirm — a live suggestion and not noise.</summary>
    public static bool IsConfirmableHigh(DealReviewView d) =>
        d.Confidence == MatchConfidence.High && d.HasSuggestion && !DealReviewDisplay.IsNoise(d.Price);

    /// <summary>The step that owns <paramref name="deal"/>, given the household's demoted-deal set.</summary>
    public static ReviewStep StepOf(DealReviewView deal, IReadOnlySet<Guid> demoted)
    {
        if (demoted.Contains(deal.DealId.Value))
            return ReviewStep.Judgement;
        if (deal.Confidence == MatchConfidence.None)
            return ReviewStep.Everything;
        if (IsConfirmableHigh(deal))
            return ReviewStep.Confirm;
        return ReviewStep.Judgement;
    }
}
