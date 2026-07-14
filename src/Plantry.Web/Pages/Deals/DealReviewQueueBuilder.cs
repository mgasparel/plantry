using Plantry.Deals.Application;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// The immutable result of one queue build (q9zr.3 + q9zr.13): the active flyer's cards, the flyer rail, the
/// overall progress counts, the three step-view partitions, and the resolved active step. It carries pure
/// presentation state — no <see cref="Microsoft.AspNetCore.Http.HttpContext"/> concern — so the page model
/// copies it verbatim onto its view properties (<c>ReviewModel.ApplyQueue</c>) and the HTTP concerns
/// (fragment-vs-page, the <c>HX-Push-Url</c> header) stay at the page keyed off
/// <see cref="ActiveFlyerKey"/> / <see cref="ActiveStep"/>.
/// </summary>
public sealed record DealReviewQueueView(
    IReadOnlyList<DealReviewView> Deals,
    FlyerRail Rail,
    FlyerBlock? ActiveFlyer,
    string? ActiveFlyerKey,
    int ReviewedCount,
    int TotalCount,
    bool ShowHandoff,
    IReadOnlyList<DealReviewView> Step1Deals,
    IReadOnlyList<DealReviewView> Step2Deals,
    IReadOnlyList<DealReviewView> Step3Deals,
    ReviewStep ActiveStep,
    IReadOnlySet<Guid> UncheckedIds);

/// <summary>
/// Builds the flyer-chaptered, step-partitioned deal review queue (q9zr.3 + q9zr.13) — the presentation
/// orchestration lifted out of <c>ReviewModel</c> to keep the page model a set of thin handlers (anti-entropy:
/// the page had ~6 unrelated reasons to change; queue projection is now one of them, owned here). It is pure
/// presentation composition over <see cref="ReviewDeals"/> (domain read), <see cref="FlyerRail"/>,
/// <see cref="ReviewStepClassifier"/>, and the flow/session state (<see cref="DealsReviewFlowSession"/>) — it
/// holds no HTTP concern and executes no command, so it stays in the Web project and does NOT belong in
/// <c>Plantry.Deals.Application</c> (bounded-context discipline, ADR-010).
/// </summary>
public sealed class DealReviewQueueBuilder(ReviewDeals reviewDeals, DealsReviewFlowSession flowSession)
{
    /// <summary>
    /// Projects the pending queue into flyer blocks + progress counts, resolves the active flyer, builds the
    /// rail over the pending + done chapters (plantry-8f7v), detects the per-flyer handoff, partitions the
    /// active flyer's pending deals into the three step views over the household's demoted-deal set, and
    /// resolves the active step. A POST verb (<paramref name="autoAdvance"/>=true) advances off a step it just
    /// emptied; a GET jump keeps an explicitly-requested empty step (rendering the empty-state) so a refresh is
    /// idempotent. <see cref="DealReviewQueueView.Deals"/> is the active flyer's pending set; the rail chapters
    /// the whole queue.
    /// </summary>
    public async Task<DealReviewQueueView> BuildAsync(string? flyer, int? step, bool autoAdvance, CancellationToken ct)
    {
        var projection = await reviewDeals.ProjectPendingQueueAsync(ct);

        var activeFlyerKey = FlyerRail.ResolveActiveKey(projection.Flyers, flyer);
        var activeFlyer = projection.Flyers.FirstOrDefault(f => f.Key == activeFlyerKey);
        // The rail renders pending + Confirm-finished (done) chapters (plantry-8f7v); routing/handoff/progress
        // stay keyed off the pending-only set. FlyerRail.Build's done-last ordering places done chips last, and
        // ResolveActiveKey only ever picks a pending block, so a done chip can never become active.
        var rail = FlyerRail.Build([.. projection.Flyers, .. projection.DoneFlyers], activeFlyerKey);

        // Handoff: a specific flyer was requested but it is no longer in the pending set (its last deal was just
        // resolved), while other flyers still have work — show the done interstitial pointing at the next
        // (now-active) flyer. A null request (fresh load) never triggers it.
        var showHandoff = flyer is not null
            && projection.Flyers.All(f => f.Key != flyer)
            && projection.Flyers.Count > 0;

        var deals = activeFlyer?.Deals ?? [];

        // Partition the active flyer's pending deals into the three step views over the demoted set.
        var flow = await flowSession.GetAsync(ct);
        var step1 = deals.Where(d => ReviewStepClassifier.StepOf(d, flow.Demoted) == ReviewStep.Confirm).ToList();
        var step2 = deals.Where(d => ReviewStepClassifier.StepOf(d, flow.Demoted) == ReviewStep.Judgement).ToList();
        var step3 = deals.Where(d => ReviewStepClassifier.StepOf(d, flow.Demoted) == ReviewStep.Everything).ToList();

        var activeStep = ResolveStep(step, autoAdvance, step1, step2, step3);

        return new DealReviewQueueView(
            deals,
            rail,
            activeFlyer,
            activeFlyerKey,
            projection.ReviewedCount,
            projection.TotalCount,
            showHandoff,
            step1,
            step2,
            step3,
            activeStep,
            flow.Unchecked);
    }

    /// <summary>
    /// Resolves the active step. No request → the first step with work (entry, <c>startPhaseFor</c>). An
    /// explicit step that still has work → that step. An explicit step now empty → auto-advance to the first
    /// step with work on a POST verb (it just emptied under the user's action), else honour it on a GET jump so
    /// the empty-state renders and a refresh stays put. Folds over the three partitioned lists the same way the
    /// page model's <c>FirstNonEmptyStep</c>/<c>StepCount</c> do over their copies.
    /// </summary>
    private static ReviewStep ResolveStep(
        int? requested, bool autoAdvance,
        IReadOnlyList<DealReviewView> step1, IReadOnlyList<DealReviewView> step2, IReadOnlyList<DealReviewView> step3)
    {
        var first = FirstNonEmptyStep(step1, step2, step3);
        if (requested is null)
            return first ?? ReviewStep.Confirm;

        var req = (ReviewStep)Math.Clamp(requested.Value, 1, 3);
        if (CountFor(req, step1, step2, step3) > 0)
            return req;
        return autoAdvance ? (first ?? req) : req;
    }

    /// <summary>The first step with pending work (stepper entry / auto-advance target), or null when done.</summary>
    private static ReviewStep? FirstNonEmptyStep(
        IReadOnlyList<DealReviewView> step1, IReadOnlyList<DealReviewView> step2, IReadOnlyList<DealReviewView> step3)
    {
        foreach (var s in new[] { ReviewStep.Confirm, ReviewStep.Judgement, ReviewStep.Everything })
            if (CountFor(s, step1, step2, step3) > 0)
                return s;
        return null;
    }

    private static int CountFor(
        ReviewStep step,
        IReadOnlyList<DealReviewView> step1, IReadOnlyList<DealReviewView> step2, IReadOnlyList<DealReviewView> step3) =>
        step switch
        {
            ReviewStep.Confirm => step1.Count,
            ReviewStep.Judgement => step2.Count,
            ReviewStep.Everything => step3.Count,
            _ => 0,
        };
}
