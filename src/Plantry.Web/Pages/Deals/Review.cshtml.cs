using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Shared;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// The deal review queue (P5-8 / DJ4 / SPEC §6b) — the deal-side twin of the Intake review form. Works the
/// pending queue with the three verbs (Confirm / Correct / Reject), the confidence-shaped treatment via the
/// shared <c>_ConfidenceBadge</c>, catalog search + inline create via the shared
/// <c>_ProductSearchCreateSheet</c>, and the single suggested-product "did you mean" chip (a deal carries
/// one suggestion, never a ranked list — P5-4).
///
/// <para><b>Guided flow (q9zr.13).</b> Inside the active flyer the pending queue is worked as three jumpable
/// tier-step VIEWS (focus.html, adopted design B): step 1 confirms the sure things (a pre-checked checklist of
/// confirmable Highs), step 2 judges the calls (Lows + any demoted Highs), step 3 clears everything else (None).
/// Unchecking a High in step 1 and committing DEMOTES it to step 2 — a presentation-tier fact tracked in
/// <see cref="IReviewFlowStateStore"/> (never a column on the Deal aggregate). Every transition re-renders
/// <c>#review-region</c> from server truth; the step lives in <c>?flyer=&amp;step=</c> so a refresh is idempotent.</para>
///
/// <para><b>Two entry paths.</b> (1) The pending queue (default GET). (2) An already-confirmed / auto-matched
/// deal arriving from P5-7's active list via <c>?dealId=</c> for correction (the DJ3 → DJ4 edge) — rendered
/// as a single focused card, Correctable/Rejectable.</para>
///
/// <para><b>Verb → command (all through P5-5, no new domain logic).</b> Confirm accepts the suggestion via
/// <see cref="ConfirmDeal.ConfirmAsync"/> using the deal's <b>own server-side</b> <c>SuggestedProductId</c>
/// (a client can't inject an arbitrary product through Confirm). Correct re-resolves to a searched or
/// inline-created product via <see cref="ConfirmDeal.CorrectAsync"/>, which supersedes for both a Pending
/// and an already-Confirmed deal. Reject flows through <see cref="RejectDeal.RejectAsync"/> (no observation).</para>
/// </summary>
[Authorize]
public sealed class ReviewModel(
    ReviewDeals reviewDeals,
    ConfirmDeal confirmDeal,
    RejectDeal rejectDeal,
    IReviewFlowStateStore flowStore,
    ICatalogProductReader catalogProducts,
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant,
    ILogger<ReviewModel> logger) : PageModel
{
    /// <summary>
    /// The deal cards to render — the <b>active flyer's</b> pending deals (queue path), or a single deal
    /// for a focused correction (<c>?dealId=</c> path). The rail chapters the whole pending queue; the card
    /// list below it only ever shows the one flyer being reviewed.
    /// </summary>
    public IReadOnlyList<DealReviewView> Deals { get; private set; } = [];

    /// <summary>The flyer rail (q9zr.3) — density-switching chapters over the whole pending queue.</summary>
    public FlyerRail Rail { get; private set; } = new([]);

    /// <summary>The active flyer being reviewed (its deals populate <see cref="Deals"/>), or null.</summary>
    public FlyerBlock? ActiveFlyer { get; private set; }

    /// <summary>The active flyer's routing key — threaded onto each verb so a re-render stays on this flyer.</summary>
    public string? ActiveFlyerKey { get; private set; }

    /// <summary>Overall reviewed count (N) for the "N of M reviewed" progress header.</summary>
    public int ReviewedCount { get; private set; }

    /// <summary>Overall total (M) — in-window Pending+Confirmed — for the progress header.</summary>
    public int TotalCount { get; private set; }

    /// <summary>Reviewed percentage for the overall <c>_Meter</c> fill (0 when nothing is in window).</summary>
    public int OverallPercent => TotalCount == 0 ? 0 : (int)Math.Round(ReviewedCount * 100.0 / TotalCount);

    /// <summary>True on the focused single-deal correction path — suppresses the rail/progress chrome.</summary>
    public bool IsSingleCorrection { get; private set; }

    /// <summary>
    /// True when the requested flyer has just been fully reviewed but other flyers still have work — render
    /// the per-flyer done interstitial handing off to <see cref="ActiveFlyer"/> (the next flyer) instead of
    /// its cards.
    /// </summary>
    public bool ShowHandoff { get; private set; }

    // ── Guided-flow step views (q9zr.13) ─────────────────────────────────────────

    /// <summary>Step 1 — "Confirm the sure things": the confirmable Highs (has a suggestion, not noise, not demoted).</summary>
    public IReadOnlyList<DealReviewView> Step1Deals { get; private set; } = [];

    /// <summary>Step 2 — "Judgement calls": pending Lows, demoted Highs, and non-confirmable Highs (e.g. a noise row).</summary>
    public IReadOnlyList<DealReviewView> Step2Deals { get; private set; } = [];

    /// <summary>Step 3 — "Everything else": the pending None ("Not in your catalog") deals.</summary>
    public IReadOnlyList<DealReviewView> Step3Deals { get; private set; } = [];

    /// <summary>The active step view (the one whose content renders under the stepper).</summary>
    public ReviewStep ActiveStep { get; private set; } = ReviewStep.Confirm;

    /// <summary>Deal ids currently unchecked in the step-1 checklist — drives each checkbox's initial state.</summary>
    public IReadOnlySet<Guid> UncheckedIds { get; private set; } = new HashSet<Guid>();

    /// <summary>The pending-deal count owned by <paramref name="step"/> — the stepper chip (✓ when 0).</summary>
    public int StepCount(ReviewStep step) => step switch
    {
        ReviewStep.Confirm => Step1Deals.Count,
        ReviewStep.Judgement => Step2Deals.Count,
        ReviewStep.Everything => Step3Deals.Count,
        _ => 0,
    };

    /// <summary>The first step with pending work (stepper entry / auto-advance target), or null when the flyer is done.</summary>
    public ReviewStep? FirstNonEmptyStep()
    {
        foreach (var s in new[] { ReviewStep.Confirm, ReviewStep.Judgement, ReviewStep.Everything })
            if (StepCount(s) > 0)
                return s;
        return null;
    }

    /// <summary>The first non-empty step OTHER than the active one — the empty-state "there is still work in step N" pointer.</summary>
    public ReviewStep? NextWorkStep()
    {
        foreach (var s in new[] { ReviewStep.Confirm, ReviewStep.Judgement, ReviewStep.Everything })
            if (s != ActiveStep && StepCount(s) > 0)
                return s;
        return null;
    }

    /// <summary>Unit options for the inline-create sheet's Defaults collapsible (Unit is required on create).</summary>
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    /// <summary>Category options for the inline-create sheet's Defaults collapsible (Category is optional).</summary>
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];

    // ── GET ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the pending review queue at the requested step, or — when <paramref name="dealId"/> is supplied
    /// (the active-list correction edge) — a single focused card for that deal. An unknown/rejected deal id
    /// falls back to the queue. A GET never auto-advances: an explicit <c>?step=</c> is honoured even when empty
    /// (it renders the empty-state), so a refresh is idempotent.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(Guid? dealId, string? flyer, int? step, CancellationToken ct = default)
    {
        if (dealId is { } id)
        {
            await LoadSheetOptionsAsync(ct);
            var one = await reviewDeals.FindAsync(DealId.From(id), ct);
            if (one is not null)
            {
                IsSingleCorrection = true;
                Deals = [one];
                return Page();
            }
            // Unknown/rejected — nothing to correct; drop to the queue.
            return RedirectToPage("./Review");
        }

        await BuildQueueAsync(flyer, step, autoAdvance: false, ct);

        // Flyer / step navigation is an htmx swap of #review-region — return just the fragment. A full
        // navigation / refresh (?flyer=&step= in the address bar) renders the whole page, so the deep-link is
        // idempotent. Same HX-Request detection as Recipes/Index and Intake/Review.
        return Request.Headers.ContainsKey("HX-Request")
            ? Partial("_ReviewQueue", this)
            : Page();
    }

    // ── GET (product search — the Correct sheet, htmx) ──────────────────────────

    /// <summary>
    /// Returns <c>&lt;li role="option"&gt;</c> markup for the shared product-search sheet used by Correct.
    /// Ranks the household's stock-eligible catalog candidates (the same pool the matcher can suggest from)
    /// with the shared <see cref="ProductNameMatcher"/>, so results match every other sheet in the app.
    /// </summary>
    public async Task<IActionResult> OnGetSearchProductsAsync(string q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Content("", "text/html");

        var candidates = await catalogProducts.ListCandidatesAsync(ct);
        var hits = ProductNameMatcher.Rank(candidates.Select(c => (c.Id, c.Name)), q.Trim());
        var enc = HtmlEncoder.Default;

        var html = string.Join("", hits.Select((h, i) =>
        {
            var label = ProductNameMatcher.RankLabel(h.Score, isTopHit: i == 0);
            return $$"""<li role="option" data-value="{{h.Id}}" data-name="{{enc.Encode(h.Name)}}" data-track="true" @click="query = $el.dataset.name; open = false; $dispatch('pick-product', {value: $el.dataset.value, name: $el.dataset.name, track: 'true'})">{{enc.Encode(h.Name)}}<span class="rk">{{enc.Encode(label)}}</span></li>""";
        }));
        return Content(html, "text/html");
    }

    // ── POST (step-1 checklist — persist an uncheck across step round-trips, q9zr.13) ──

    /// <summary>
    /// Persists whether one step-1 High is currently unchecked, so jumping away and back (or refreshing) does
    /// not re-check it — the prototype bug the adopted design fixed (<c>d.unchecked</c> in focus.html). Fired
    /// by the checkbox's <c>change</c> event; returns 204 (the client keeps the checkbox as the user left it
    /// and Alpine updates the "Confirm N" count — no re-render needed).
    /// </summary>
    public async Task<IActionResult> OnPostSetCheckAsync(
        [FromForm] Guid dealId, [FromForm] bool isChecked, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        await EnsureSessionStartedAsync(ct);
        await flowStore.SetUncheckedAsync(FlowStoreKey(), dealId, isUnchecked: !isChecked, ct);
        return new NoContentResult();
    }

    // ── POST (verbs — each re-renders the queue fragment from server truth) ──────

    /// <summary>Confirm: accept the AI suggestion. The suggested product id is read server-side from the deal.</summary>
    public async Task<IActionResult> OnPostConfirmAsync(
        [FromQuery] Guid dealId, [FromQuery] string? flyer, [FromQuery] int? step, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        var view = await reviewDeals.FindAsync(DealId.From(dealId), ct);
        if (view is null)
            return await QueueFragmentAsync(flyer, step, ct);

        if (view.SuggestedProductId is not { } productId)
        {
            // A None/"Unrecognized" deal has no suggestion to accept — the UI never offers Confirm here.
            logger.LogWarning("Confirm rejected for deal {DealId}: no suggested product to accept.", dealId);
            return await QueueFragmentAsync(flyer, step, ct);
        }

        var result = await confirmDeal.ConfirmAsync(DealId.From(dealId), productId, CurrentUserId, ct);
        if (result.IsFailure)
            logger.LogWarning("Confirm deal {DealId} failed: {ErrorCode}.", dealId, result.Error.Code);

        return await QueueFragmentAsync(flyer, step, ct);
    }

    /// <summary>
    /// Correct: re-resolve to a searched or inline-created product and supersede. Handles a Pending deal
    /// and an already-confirmed auto-matched deal uniformly (<see cref="ConfirmDeal.CorrectAsync"/>).
    /// </summary>
    public async Task<IActionResult> OnPostCorrectAsync(
        [FromForm] Guid dealId,
        [FromForm] Guid? productId,
        [FromForm] string? newProductName,
        [FromForm] Guid? newProductUnitId,
        [FromForm] Guid? newProductCategoryId,
        [FromForm] string? flyer,
        [FromForm] int? step,
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        Guid resolvedProductId;
        if (productId is { } existing && existing != Guid.Empty)
        {
            resolvedProductId = existing;
        }
        else if (!string.IsNullOrWhiteSpace(newProductName) && newProductUnitId is { } unitId && unitId != Guid.Empty)
        {
            // Inline create (§2d twin): mint the catalog product, then correct the deal against it. The
            // create is a Web composition-root orchestration (Catalog owns product creation), mirroring
            // Intake's CreateProductAdapter — the deal never creates a product in its own domain.
            var created = await CreateProductAsync(newProductName.Trim(), unitId, newProductCategoryId, ct);
            if (created.IsFailure)
            {
                logger.LogWarning(
                    "Inline product create failed while correcting deal {DealId}: {ErrorCode}.", dealId, created.Error.Code);
                return await QueueFragmentAsync(flyer, step, ct);
            }
            resolvedProductId = created.Value;
        }
        else
        {
            logger.LogWarning("Correct deal {DealId} rejected: neither an existing nor a new product was supplied.", dealId);
            return await QueueFragmentAsync(flyer, step, ct);
        }

        var result = await confirmDeal.CorrectAsync(DealId.From(dealId), resolvedProductId, CurrentUserId, ct);
        if (result.IsFailure)
            logger.LogWarning("Correct deal {DealId} failed: {ErrorCode}.", dealId, result.Error.Code);

        return await QueueFragmentAsync(flyer, step, ct);
    }

    /// <summary>Reject: leave the queue, write no price observation (D5).</summary>
    public async Task<IActionResult> OnPostRejectAsync(
        [FromQuery] Guid dealId, [FromQuery] string? flyer, [FromQuery] int? step, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        var result = await rejectDeal.RejectAsync(DealId.From(dealId), CurrentUserId, ct: ct);
        if (result.IsFailure)
            logger.LogWarning("Reject deal {DealId} failed: {ErrorCode}.", dealId, result.Error.Code);

        return await QueueFragmentAsync(flyer, step, ct);
    }

    // ── POST (bulk verbs — server-side loops over the single verbs; q9zr.4 + q9zr.13) ──

    /// <summary>
    /// Confirm all / step-1 commit: accept the pending High-confidence deals of the active flyer through the
    /// single-deal <see cref="ConfirmDeal.ConfirmAsync"/> — each with <b>that deal's own server-side</b>
    /// <c>SuggestedProductId</c>, so a client can never inject a product. Bulk is a server-side loop over the
    /// existing verb: no new domain logic. Noise rows (price ≤ 0) are never in the eligible set.
    /// <para>
    /// The step-1 checklist posts <paramref name="checklistCommit"/>=true with only the CHECKED
    /// <paramref name="dealIds"/>: those are confirmed and every eligible High NOT checked is <b>demoted</b> to
    /// step 2 (q9zr.13). With no flag, the legacy q9zr.4 semantics hold — an absent/empty <paramref name="dealIds"/>
    /// means the whole eligible set (bulk confirm-all), and nothing is demoted. Idempotent: a re-POST after the
    /// re-render finds nothing eligible and is a no-op.
    /// </para>
    /// </summary>
    public async Task<IActionResult> OnPostConfirmAllAsync(
        [FromQuery] string? flyer, [FromQuery] int? step,
        [FromForm(Name = "dealIds")] Guid[]? dealIds, [FromForm] bool checklistCommit = false,
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        await EnsureSessionStartedAsync(ct);

        var eligible = await EligibleActiveFlyerDealsAsync(
            flyer,
            d => ReviewStepClassifier.IsConfirmableHigh(d),
            ct);

        IReadOnlyList<DealReviewView> toConfirm;
        IReadOnlyList<DealReviewView> toDemote = [];
        if (checklistCommit)
        {
            // Step-1 commit: confirm exactly the checked ids; demote every other eligible High to step 2. An
            // empty checked set is a real "confirm none, demote all" — NOT the legacy "whole set" shorthand.
            var checkedSet = (dealIds ?? []).ToHashSet();
            toConfirm = eligible.Where(d => checkedSet.Contains(d.DealId.Value)).ToList();
            toDemote = eligible.Where(d => !checkedSet.Contains(d.DealId.Value)).ToList();
        }
        else
        {
            toConfirm = ScopeToRequestedIds(eligible, dealIds, verb: "ConfirmAll");
        }

        var confirmed = 0;
        foreach (var deal in toConfirm)
        {
            // Per-deal server-side suggestion — identical semantics to the single Confirm verb.
            var productId = deal.SuggestedProductId!.Value;
            var result = await confirmDeal.ConfirmAsync(deal.DealId, productId, CurrentUserId, ct);
            if (result.IsSuccess)
                confirmed++;
            else
                logger.LogWarning("ConfirmAll: deal {DealId} failed: {ErrorCode}.", deal.DealId.Value, result.Error.Code);
        }

        if (checklistCommit)
        {
            // Demote the unchecked Highs into step 2's pool, and clear the whole eligible set's checkbox state
            // (the confirmed Highs left the queue; the demoted ones now live in step 2 with no checkbox).
            await flowStore.CommitAsync(
                FlowStoreKey(),
                demote: toDemote.Select(d => d.DealId.Value),
                clearUnchecked: eligible.Select(d => d.DealId.Value),
                ct);
        }

        if (confirmed > 0)
            SetBulkToast($"Confirmed {confirmed} {(confirmed == 1 ? "match" : "matches")}");

        return await QueueFragmentAsync(flyer, step, ct);
    }

    /// <summary>
    /// Dismiss all: reject every pending None-confidence ("Not in your catalog") deal in the active flyer
    /// through the single-deal <see cref="RejectDeal.RejectAsync"/> (writes no observation, D5). Bulk is a
    /// server-side loop over the existing verb. Noise rows (price ≤ 0) are <b>included</b> — they are
    /// None-tier flyer clutter the user clears in one action. Optional <paramref name="dealIds"/> narrows the
    /// set (the browsable-list bead, q9zr.5, posts filter-scoped ids); ids outside the eligible None set of
    /// this flyer are ignored and logged. Idempotent: a re-POST after the re-render finds nothing eligible.
    /// </summary>
    public async Task<IActionResult> OnPostDismissAllAsync(
        [FromQuery] string? flyer, [FromQuery] int? step,
        [FromForm(Name = "dealIds")] Guid[]? dealIds, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        var eligible = await EligibleActiveFlyerDealsAsync(
            flyer, d => d.Confidence == MatchConfidence.None, ct); // None tier includes noise rows
        eligible = ScopeToRequestedIds(eligible, dealIds, verb: "DismissAll");

        var dismissed = 0;
        foreach (var deal in eligible)
        {
            var result = await rejectDeal.RejectAsync(deal.DealId, CurrentUserId, ct: ct);
            if (result.IsSuccess)
                dismissed++;
            else
                logger.LogWarning("DismissAll: deal {DealId} failed: {ErrorCode}.", deal.DealId.Value, result.Error.Code);
        }

        if (dismissed > 0)
            SetBulkToast($"Dismissed {dismissed} {(dismissed == 1 ? "deal" : "deals")}");

        return await QueueFragmentAsync(flyer, step, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The active flyer's pending deals matching <paramref name="predicate"/> — the eligible set a bulk verb
    /// loops over. Resolves the active flyer the same way the queue render does (the requested key when it
    /// still has work, else the default soonest-expiring one), so the bulk verb acts on exactly the tier the
    /// user is looking at. Empty when nothing is pending.
    /// </summary>
    private async Task<IReadOnlyList<DealReviewView>> EligibleActiveFlyerDealsAsync(
        string? flyer, Func<DealReviewView, bool> predicate, CancellationToken ct)
    {
        var projection = await reviewDeals.ProjectPendingQueueAsync(ct);
        var activeKey = FlyerRail.ResolveActiveKey(projection.Flyers, flyer);
        var activeFlyer = projection.Flyers.FirstOrDefault(f => f.Key == activeKey);
        return activeFlyer is null
            ? []
            : activeFlyer.Deals.Where(predicate).ToList();
    }

    /// <summary>
    /// Intersects the eligible set with an optional client-supplied <paramref name="dealIds"/> scope. Absent
    /// (or empty) means "the whole eligible set". Any requested id outside the eligible tier/flyer is dropped
    /// and logged — a client can never widen the set or reach a deal in another tier through the id list.
    /// </summary>
    private IReadOnlyList<DealReviewView> ScopeToRequestedIds(
        IReadOnlyList<DealReviewView> eligible, Guid[]? dealIds, string verb)
    {
        if (dealIds is null || dealIds.Length == 0)
            return eligible;

        var requested = dealIds.ToHashSet();
        var scoped = eligible.Where(d => requested.Contains(d.DealId.Value)).ToList();

        var ignored = requested.Where(id => scoped.All(d => d.DealId.Value != id)).ToList();
        if (ignored.Count > 0)
            logger.LogInformation(
                "{Verb}: ignored {Count} requested id(s) outside the eligible set: {Ids}.",
                verb, ignored.Count, string.Join(", ", ignored));

        return scoped;
    }

    /// <summary>
    /// Fires the shared toast (q9zr.1) with plain status feedback via an <c>HX-Trigger</c> response header.
    /// htmx dispatches the event on the triggering button before the swap; it bubbles to the persistent
    /// Alpine host, which flashes the <c>.toast</c> primitive. No undo affordance — ruled out for P1 (q9zr.9).
    /// </summary>
    private void SetBulkToast(string message) =>
        Response.Headers["HX-Trigger"] = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object> { ["deals-bulk-done"] = new { message } });


    private async Task<Result<Guid>> CreateProductAsync(string name, Guid unitId, Guid? categoryId, CancellationToken ct)
    {
        var command = new CreateProductCommand(
            name, unitId, categoryId is { } c && c != Guid.Empty ? c : null, defaultLocationId: null,
            products, units, categories, locations, clock, tenant);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            return result.Error;
        return result.Value.Value;
    }

    private async Task<IActionResult> QueueFragmentAsync(string? flyer, int? step, CancellationToken ct)
    {
        await BuildQueueAsync(flyer, step, autoAdvance: true, ct);
        return Partial("_ReviewQueue", this);
    }

    /// <summary>
    /// Builds the flyer-chaptered, step-partitioned queue view (q9zr.3 + q9zr.13): projects the pending queue
    /// into flyer blocks + progress counts, resolves the active flyer, partitions its pending deals into the
    /// three step views over the household's demoted-deal set, and resolves the active step from
    /// <paramref name="step"/>. A POST verb (<paramref name="autoAdvance"/>=true) advances off a step it just
    /// emptied; a GET jump keeps an explicitly-requested empty step (rendering the empty-state) so a refresh is
    /// idempotent. <see cref="Deals"/> is the active flyer's pending set; the rail chapters the whole queue.
    /// </summary>
    private async Task BuildQueueAsync(string? flyer, int? step, bool autoAdvance, CancellationToken ct)
    {
        await LoadSheetOptionsAsync(ct);
        await EnsureSessionStartedAsync(ct);

        var projection = await reviewDeals.ProjectPendingQueueAsync(ct);
        ReviewedCount = projection.ReviewedCount;
        TotalCount = projection.TotalCount;

        ActiveFlyerKey = FlyerRail.ResolveActiveKey(projection.Flyers, flyer);
        ActiveFlyer = projection.Flyers.FirstOrDefault(f => f.Key == ActiveFlyerKey);
        // The rail renders pending + Confirm-finished (done) chapters (plantry-8f7v); routing/handoff/progress
        // above stay keyed off the pending-only set. FlyerRail.Build's done-last ordering places done chips last,
        // and ResolveActiveKey only ever picks a pending block, so a done chip can never become active.
        Rail = FlyerRail.Build([.. projection.Flyers, .. projection.DoneFlyers], ActiveFlyerKey);

        // Handoff: a specific flyer was requested but it is no longer in the pending set (its last deal was
        // just resolved), while other flyers still have work — show the done interstitial pointing at the
        // next (now-active) flyer. A null request (fresh load) never triggers it.
        ShowHandoff = flyer is not null
            && projection.Flyers.All(f => f.Key != flyer)
            && projection.Flyers.Count > 0;

        Deals = ActiveFlyer?.Deals ?? [];

        // Partition the active flyer's pending deals into the three step views over the demoted set.
        var flow = await flowStore.GetAsync(FlowStoreKey(), ct);
        UncheckedIds = flow.Unchecked;
        Step1Deals = Deals.Where(d => ReviewStepClassifier.StepOf(d, flow.Demoted) == ReviewStep.Confirm).ToList();
        Step2Deals = Deals.Where(d => ReviewStepClassifier.StepOf(d, flow.Demoted) == ReviewStep.Judgement).ToList();
        Step3Deals = Deals.Where(d => ReviewStepClassifier.StepOf(d, flow.Demoted) == ReviewStep.Everything).ToList();

        ActiveStep = ResolveStep(step, autoAdvance);

        // Keep the address bar (and therefore a refresh) on the effective step. The step buttons and rail chips
        // carry their own hx-push-url for GET jumps; a POST verb that auto-advanced needs the header to update.
        if (!ShowHandoff && ActiveFlyer is not null && Request.Headers.ContainsKey("HX-Request"))
            Response.Headers["HX-Push-Url"] = $"/Deals/Review?flyer={ActiveFlyerKey}&step={(int)ActiveStep}";
    }

    /// <summary>
    /// Resolves the active step. No request → the first step with work (entry, <c>startPhaseFor</c>). An
    /// explicit step that still has work → that step. An explicit step now empty → auto-advance to the first
    /// step with work on a POST verb (it just emptied under the user's action), else honour it on a GET jump so
    /// the empty-state renders and a refresh stays put.
    /// </summary>
    private ReviewStep ResolveStep(int? requested, bool autoAdvance)
    {
        var first = FirstNonEmptyStep();
        if (requested is null)
            return first ?? ReviewStep.Confirm;

        var req = (ReviewStep)Math.Clamp(requested.Value, 1, 3);
        if (StepCount(req) > 0)
            return req;
        return autoAdvance ? (first ?? req) : req;
    }

    private async Task LoadSheetOptionsAsync(CancellationToken ct)
    {
        UnitOptions = (await units.ListAsync(ct))
            .OrderBy(u => u.Code, StringComparer.OrdinalIgnoreCase)
            .Select(u => new SelectListItem(u.Code, u.Id.Value.ToString()))
            .ToList();

        CategoryOptions = (await categories.ListActiveAsync(ct))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new SelectListItem(c.Name, c.Id.Value.ToString()))
            .ToList();
    }

    /// <summary>
    /// The Correct sheet's search endpoint URL — emitted into the shared sheet's <c>SearchUrl</c>.
    /// </summary>
    public string SearchUrl => Url.Page("./Review", "SearchProducts")!;

    // ── Guided-flow presentation-state store (q9zr.13) ────────────────────────────

    /// <summary>
    /// Ensures the ASP.NET Core session is started and the <c>.AspNetCore.Session</c> cookie is issued so
    /// <see cref="Microsoft.AspNetCore.Http.ISession.Id"/> is stable across requests — otherwise the id
    /// regenerates each request and the flow-state store key rotates (the SO5.2 session-key bug). Writing a
    /// sentinel byte forces cookie issuance. Must run before <see cref="FlowStoreKey"/> on every read/write.
    /// </summary>
    private async Task EnsureSessionStartedAsync(CancellationToken ct = default)
    {
        await HttpContext.Session.LoadAsync(ct);
        if (!HttpContext.Session.TryGetValue("_drf", out _))
            HttpContext.Session.Set("_drf", [0x01]);
    }

    /// <summary>
    /// The guided-flow state store key: <c>deals-review-flow_{householdId:N}_{sessionId}</c>. Presentation
    /// state (demoted + unchecked deal ids) is per household + browser session — see <see cref="IReviewFlowStateStore"/>.
    /// </summary>
    private string FlowStoreKey() =>
        $"deals-review-flow_{tenant.HouseholdId ?? Guid.Empty:N}_{HttpContext.Session.Id}";

    // Defensive parse: under [Authorize] the NameIdentifier claim is always present, but a missing/malformed
    // claim degrades to Guid.Empty (the same "no principal" sentinel) rather than throwing a 500.
    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
