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
/// <para><b>Two entry paths.</b> (1) The pending queue (default GET). (2) An already-confirmed / auto-matched
/// deal arriving from P5-7's active list via <c>?dealId=</c> for correction (the DJ3 → DJ4 edge) — rendered
/// as a single focused card, Correctable/Rejectable.</para>
///
/// <para><b>Verb → command (all through P5-5, no new domain logic).</b> Confirm accepts the suggestion via
/// <see cref="ConfirmDeal.ConfirmAsync"/> using the deal's <b>own server-side</b> <c>SuggestedProductId</c>
/// (a client can't inject an arbitrary product through Confirm). Correct re-resolves to a searched or
/// inline-created product via <see cref="ConfirmDeal.CorrectAsync"/>, which supersedes for both a Pending
/// and an already-Confirmed deal. Reject flows through <see cref="RejectDeal.RejectAsync"/> (no observation).</para>
///
/// <para><b>htmx + server truth.</b> Each verb re-renders the whole <c>#review-region</c> fragment from
/// server state (the <c>Settings/StoresAndDeals</c> pattern), so a re-drive / refresh never double-acts.
/// This is a plain htmx + Alpine surface — not one of the three sanctioned reactive islands (ADR-020).</para>
/// </summary>
[Authorize]
public sealed class ReviewModel(
    ReviewDeals reviewDeals,
    ConfirmDeal confirmDeal,
    RejectDeal rejectDeal,
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

    /// <summary>Unit options for the inline-create sheet's Defaults collapsible (Unit is required on create).</summary>
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    /// <summary>Category options for the inline-create sheet's Defaults collapsible (Category is optional).</summary>
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];

    // ── GET ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the pending review queue, or — when <paramref name="dealId"/> is supplied (the active-list
    /// correction edge) — a single focused card for that deal. An unknown/rejected deal id falls back to
    /// the queue.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(Guid? dealId, string? flyer, CancellationToken ct = default)
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

        await BuildQueueAsync(flyer, ct);

        // Flyer navigation (rail chip/pill click) is an htmx swap of #review-region — return just the
        // fragment. A full navigation / refresh (?flyer= in the address bar) renders the whole page, so the
        // deep-link is idempotent. Same HX-Request detection as Recipes/Index and Intake/Review.
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

    // ── POST (verbs — each re-renders the queue fragment from server truth) ──────

    /// <summary>Confirm: accept the AI suggestion. The suggested product id is read server-side from the deal.</summary>
    public async Task<IActionResult> OnPostConfirmAsync(
        [FromQuery] Guid dealId, [FromQuery] string? flyer, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        var view = await reviewDeals.FindAsync(DealId.From(dealId), ct);
        if (view is null)
            return await QueueFragmentAsync(flyer, ct);

        if (view.SuggestedProductId is not { } productId)
        {
            // A None/"Unrecognized" deal has no suggestion to accept — the UI never offers Confirm here.
            logger.LogWarning("Confirm rejected for deal {DealId}: no suggested product to accept.", dealId);
            return await QueueFragmentAsync(flyer, ct);
        }

        var result = await confirmDeal.ConfirmAsync(DealId.From(dealId), productId, CurrentUserId, ct);
        if (result.IsFailure)
            logger.LogWarning("Confirm deal {DealId} failed: {ErrorCode}.", dealId, result.Error.Code);

        return await QueueFragmentAsync(flyer, ct);
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
                return await QueueFragmentAsync(flyer, ct);
            }
            resolvedProductId = created.Value;
        }
        else
        {
            logger.LogWarning("Correct deal {DealId} rejected: neither an existing nor a new product was supplied.", dealId);
            return await QueueFragmentAsync(flyer, ct);
        }

        var result = await confirmDeal.CorrectAsync(DealId.From(dealId), resolvedProductId, CurrentUserId, ct);
        if (result.IsFailure)
            logger.LogWarning("Correct deal {DealId} failed: {ErrorCode}.", dealId, result.Error.Code);

        return await QueueFragmentAsync(flyer, ct);
    }

    /// <summary>Reject: leave the queue, write no price observation (D5).</summary>
    public async Task<IActionResult> OnPostRejectAsync(
        [FromQuery] Guid dealId, [FromQuery] string? flyer, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Forbid();

        var result = await rejectDeal.RejectAsync(DealId.From(dealId), CurrentUserId, ct: ct);
        if (result.IsFailure)
            logger.LogWarning("Reject deal {DealId} failed: {ErrorCode}.", dealId, result.Error.Code);

        return await QueueFragmentAsync(flyer, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

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

    private async Task<IActionResult> QueueFragmentAsync(string? flyer, CancellationToken ct)
    {
        await BuildQueueAsync(flyer, ct);
        return Partial("_ReviewQueue", this);
    }

    /// <summary>
    /// Builds the flyer-chaptered queue view (q9zr.3): projects the pending queue into flyer blocks +
    /// progress counts, resolves the active flyer from <paramref name="flyer"/> (default = soonest-expiring
    /// pending), and detects the per-flyer handoff. <see cref="Deals"/> is scoped to the active flyer so the
    /// card list renders one chapter at a time; the rail chapters the whole queue.
    /// </summary>
    private async Task BuildQueueAsync(string? flyer, CancellationToken ct)
    {
        await LoadSheetOptionsAsync(ct);

        var projection = await reviewDeals.ProjectPendingQueueAsync(ct);
        ReviewedCount = projection.ReviewedCount;
        TotalCount = projection.TotalCount;

        ActiveFlyerKey = FlyerRail.ResolveActiveKey(projection.Flyers, flyer);
        ActiveFlyer = projection.Flyers.FirstOrDefault(f => f.Key == ActiveFlyerKey);
        Rail = FlyerRail.Build(projection.Flyers, ActiveFlyerKey);

        // Handoff: a specific flyer was requested but it is no longer in the pending set (its last deal was
        // just resolved), while other flyers still have work — show the done interstitial pointing at the
        // next (now-active) flyer. A null request (fresh load) never triggers it.
        ShowHandoff = flyer is not null
            && projection.Flyers.All(f => f.Key != flyer)
            && projection.Flyers.Count > 0;

        Deals = ActiveFlyer?.Deals ?? [];
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

    // Defensive parse: under [Authorize] the NameIdentifier claim is always present, but a missing/malformed
    // claim degrades to Guid.Empty (the same "no principal" sentinel) rather than throwing a 500.
    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
