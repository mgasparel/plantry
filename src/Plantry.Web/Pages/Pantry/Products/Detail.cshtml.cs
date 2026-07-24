using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Shared;

namespace Plantry.Web.Pages.Pantry.Products;

[Authorize]
public sealed class DetailModel(
    InventoryQueryService queries,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    ICatalogReadFacade catalog,
    IUnitRepository units,
    ILocationRepository locations,
    IStockProvenanceReader provenance,
    IPriceObservationRepository priceRepository,
    IUnitPriceCalculator priceCalculator,
    PricingQueries pricingQueries,
    ProductQueryService catalogProducts,
    RecipesUsingProductQuery recipeUsages,
    DisplayCurrencyAccessor displayCurrency,
    IClock clock,
    ITenantContext tenant,
    ILogger<ConsumeStockCommand> consumeLogger,
    ILogger<SetLowStockThresholdCommand> thresholdLogger,
    ILogger<AddStockCommand> addStockLogger,
    ILogger<RecordObservationCommand> priceLogger,
    ILogger<MarkStockOpenedCommand> markOpenedLogger,
    ILogger<UnmarkStockOpenedCommand> unmarkOpenedLogger,
    ILogger<TransferStockCommand> transferLogger) : PageModel
{
    public Guid ProductId { get; private set; }
    public ProductStockDetail? Detail { get; private set; }

    /// <summary>Recipes that directly reference this product (plantry-o0r8) — either as a consumer
    /// ("Used in") or as the recipe's declared cook yield ("Made by"). Loaded once on the initial GET;
    /// no consume/threshold/price action changes this list, so the POST handlers' partial reloads don't
    /// re-fetch it.</summary>
    public IReadOnlyList<RecipeProductUsage> RecipeUsages { get; private set; } = [];

    /// <summary>The current effective price (deal-aware — same read model <c>CostingService</c> uses via
    /// <c>PricingQueries.EffectivePriceAsync</c>), rendered as "£3.99 for 500 g" — or a muted placeholder
    /// when nothing has ever been observed for this product (plantry-3fqm).</summary>
    public string PriceDisplayText { get; private set; } = "No price recorded yet";

    /// <summary>
    /// Resolved provenance chips (receipt-intake-history.md H11) for <see cref="Detail"/>'s History rows,
    /// keyed by <see cref="StockJournalRow.JournalId"/>. Only Intake/Cook rows are offered to the reader —
    /// Manual rows keep today's plain text unconditionally. A row absent from this dictionary (unresolved,
    /// or not attempted) renders its existing plain-text fallback.
    /// </summary>
    public IReadOnlyDictionary<Guid, ProvenanceChip> Chips { get; private set; } =
        new Dictionary<Guid, ProvenanceChip>();

    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    /// <summary>Locations for the zero-stock landing's Add stock sheet (plantry-sjfn) — the only sheet
    /// on this page that needs a location picker, so it's loaded on demand rather than every GET.</summary>
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];

    [BindProperty]
    public ConsumeInputModel Input { get; set; } = new();

    [BindProperty]
    public ThresholdInputModel ThresholdInput { get; set; } = new();

    [BindProperty]
    public PriceInputModel PriceInput { get; set; } = new();

    /// <summary>Add stock from the product's own detail page (plantry-sjfn) — the primary CTA on the
    /// zero-stock landing (and available any time). No product picker: the product is this page's id.</summary>
    [BindProperty]
    public AddStockInputModel AddStockInput { get; set; } = new();

    /// <summary>Move/transfer a lot between locations, with implicit freeze/thaw expiry recompute
    /// (plantry-6owm). Defaults to the full lot quantity; a partial quantity splits.</summary>
    [BindProperty]
    public MoveInputModel MoveInput { get; set; } = new();

    /// <summary>Populated by <see cref="OnGetMoveSheetAsync"/>/<see cref="ReloadMoveSheetAsync"/> — the
    /// data the Move sheet's Alpine component needs for its live split/effect preview. Null outside
    /// those two handlers.</summary>
    public MoveSheetViewModel? MoveSheet { get; private set; }

    public sealed class ConsumeInputModel
    {
        [Required(ErrorMessage = "Enter an amount.")]
        [Range(0.000001, double.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal? Amount { get; set; }

        [Required(ErrorMessage = "Choose a unit.")]
        public Guid? UnitId { get; set; }

        [Required]
        public StockReason Reason { get; set; } = StockReason.Consumed;

        public Guid? TargetEntryId { get; set; }
    }

    public sealed class ThresholdInputModel
    {
        [Range(0, double.MaxValue, ErrorMessage = "Threshold must be zero or greater.")]
        public decimal? Threshold { get; set; }
    }

    /// <summary>Manual price entry (plantry-3fqm) — price/quantity/unit only, deliberately no store
    /// picker (design decision: manual estimates carry no merchant provenance).</summary>
    public sealed class PriceInputModel
    {
        [Required(ErrorMessage = "Enter a price.")]
        [Range(0.01, double.MaxValue, ErrorMessage = "Price must be greater than zero.")]
        public decimal? Price { get; set; }

        [Required(ErrorMessage = "Enter a quantity.")]
        [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal? Quantity { get; set; } = 1m;

        [Required(ErrorMessage = "Choose a unit.")]
        public Guid? UnitId { get; set; }
    }

    /// <summary>Add stock for this exact product (plantry-sjfn) — quantity/unit/location/expiry only,
    /// mirroring <c>Pantry.IndexModel.AddStockInputModel</c> minus the product picker.</summary>
    public sealed class AddStockInputModel
    {
        [Required(ErrorMessage = "Enter a quantity.")]
        [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal? Quantity { get; set; }

        [Required(ErrorMessage = "Choose a unit.")]
        public Guid? UnitId { get; set; }

        [Required(ErrorMessage = "Choose a location.")]
        public Guid? LocationId { get; set; }

        [DataType(DataType.Date)]
        public DateOnly? ExpiryDate { get; set; }
    }

    /// <summary>Move/transfer one lot (plantry-6owm) — quantity defaults to the full lot; a lesser
    /// amount splits the lot (rule 1). <see cref="EntryId"/> travels as a hidden field, not user
    /// input, so — mirroring <see cref="ConsumeInputModel.TargetEntryId"/> — it carries no
    /// <c>[Required]</c> validation message; <see cref="OnPostMoveAsync"/> guards it directly.</summary>
    public sealed class MoveInputModel
    {
        public Guid? EntryId { get; set; }

        [Required(ErrorMessage = "Choose a destination.")]
        public Guid? LocationId { get; set; }

        [Required(ErrorMessage = "Enter a quantity.")]
        [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal? Quantity { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ProductId = id;
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadChipsAsync(Detail);
        PriceDisplayText = await BuildPriceDisplayAsync(id);
        RecipeUsages = await recipeUsages.ExecuteAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnGetConsumeSheetAsync(Guid id, Guid? entryId)
    {
        ProductId = id;
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();

        Input = new ConsumeInputModel
        {
            TargetEntryId = entryId,
            Reason = StockReason.Consumed,
            UnitId = (await catalog.FindProductAsync(id))?.DefaultUnitId,
        };
        await LoadUnitOptionsAsync();
        return Partial("_ConsumeSheet", this);
    }

    public async Task<IActionResult> OnPostConsumeAsync(Guid id)
    {
        ProductId = id;
        ClearOtherSheetValidation(nameof(Input));
        if (!ModelState.IsValid)
            return await ReloadSheetAsync(id);

        var result = await Run(id, Input.Amount!.Value, Input.UnitId!.Value, Input.Reason, Input.TargetEntryId);
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await ReloadSheetAsync(id);
        }

        var notice = BuildConsumeNotice(result.Value);

        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadChipsAsync(Detail);
        return Partial("_StockDetail", new StockDetailPartialModel(Detail, Oob: true, Notice: notice, Chips));
    }

    /// <summary>
    /// Combines the shortfall notice with any auto-open lines (plantry-1le6 rule 5) into the single
    /// inline notice this page already shows for a consume result. A multi-lot consume can auto-open
    /// more than one lot, so each gets its own sentence.
    /// </summary>
    internal static string? BuildConsumeNotice(ConsumeOutcome outcome)
    {
        var parts = new List<string>();
        if (outcome.HasShortfall)
            parts.Add($"Consumed what was available — {outcome.ShortfallAmount:0.###} could not be satisfied.");
        foreach (var opened in outcome.AutoOpened)
        {
            parts.Add(opened.DefaultApplied
                ? $"Marked opened — now expires {opened.ExpiryDate:d MMM yyyy}."
                : "Marked opened — expiry unchanged.");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>Discards an entire lot in one action (SPEC §1c) — amount and unit are read from the
    /// aggregate server-side so they cannot be spoofed via URL manipulation.</summary>
    public async Task<IActionResult> OnPostDiscardAsync(Guid id, Guid entryId)
    {
        ProductId = id;
        var detail = await queries.FindDetailAsync(id);
        if (detail is null) return NotFound();
        var lot = detail.Lots.FirstOrDefault(l => l.EntryId == StockEntryId.From(entryId));
        if (lot is null) return NotFound();

        var result = await Run(id, lot.Quantity, lot.UnitId, StockReason.Discarded, entryId);
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadChipsAsync(Detail);
        var notice = result.IsFailure ? result.Error.Description : null;
        return Partial("_StockDetail", new StockDetailPartialModel(Detail, Oob: false, Notice: notice, Chips));
    }

    /// <summary>
    /// The "Mark opened" row action (plantry-1le6, UI spec §1) — a one-tap, no-input htmx POST. Unlike
    /// this page's other mutations (which swap <c>_StockDetail</c> in place via an OOB htmx response),
    /// this genuinely follows POST-Redirect-GET: it sets the shared save-toast
    /// (<c>TempData["ToastMessage"]</c>, plantry-u7n9/8b8802a) and responds with <c>HX-Redirect</c> so
    /// htmx does a full navigation back to this page — the same idiom <c>Intake/Upload</c> uses to hand
    /// off to a fresh GET. A rejected mark (e.g. a concurrent double-tap) still redirects, carrying the
    /// error as the toast instead, since the lot's on-screen state already reflects reality either way.
    /// </summary>
    public async Task<IActionResult> OnPostMarkOpenAsync(Guid id, Guid entryId)
    {
        var result = await new MarkStockOpenedCommand(
            id, entryId, stocks, catalog, clock, tenant, markOpenedLogger).ExecuteAsync();

        TempData["ToastMessage"] = result.IsSuccess
            ? FormatMarkOpenedToast(result.Value)
            : result.Error.Description;

        Response.Headers["HX-Redirect"] = Url.Page("./Detail", new { id })!;
        return new EmptyResult();
    }

    /// <summary>Tapping the "Open" badge un-marks the lot (plantry-1le6, UI spec §3) — same PRG/toast
    /// shape as <see cref="OnPostMarkOpenAsync"/>. Never restores the expiry opening replaced.</summary>
    public async Task<IActionResult> OnPostUnmarkOpenAsync(Guid id, Guid entryId)
    {
        var result = await new UnmarkStockOpenedCommand(
            id, entryId, stocks, clock, tenant, unmarkOpenedLogger).ExecuteAsync();

        TempData["ToastMessage"] = result.IsSuccess
            ? FormatUnmarkedToast(result.Value)
            : result.Error.Description;

        Response.Headers["HX-Redirect"] = Url.Page("./Detail", new { id })!;
        return new EmptyResult();
    }

    /// <summary>"Opened — now expires 5 Aug 2026", or "Opened — expiry unchanged" when no after-opening
    /// default is configured anywhere (product or category) — <see cref="MarkOpenedOutcome.DefaultApplied"/>
    /// is the honest signal here, not whether the date happens to differ (a tight clamp can leave it
    /// unchanged even though a default was applied).</summary>
    internal static string FormatMarkOpenedToast(MarkOpenedOutcome outcome) =>
        outcome.DefaultApplied
            ? $"Opened — now expires {outcome.ExpiryDate:d MMM yyyy}"
            : "Opened — expiry unchanged";

    /// <summary>"Unmarked — expiry stays 5 Aug 2026" — stated honestly: un-marking never recomputes,
    /// so whatever expiry opening left behind is what remains.</summary>
    internal static string FormatUnmarkedToast(UnmarkOpenedOutcome outcome) =>
        outcome.ExpiryDate is { } expiry
            ? $"Unmarked — expiry stays {expiry:d MMM yyyy}"
            : "Unmarked — no expiry set";

    /// <summary>Opens the Move sheet (plantry-6owm) — the third per-lot row action, beside Use/Discard.
    /// Mirrors <see cref="OnGetConsumeSheetAsync"/>'s shape.</summary>
    public async Task<IActionResult> OnGetMoveSheetAsync(Guid id, Guid entryId)
    {
        ProductId = id;
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();

        var moveSheet = await BuildMoveSheetAsync(id, entryId);
        if (moveSheet is null) return NotFound();
        MoveSheet = moveSheet;

        MoveInput = new MoveInputModel
        {
            EntryId = entryId,
            LocationId = moveSheet.DefaultDestinationId,
            Quantity = moveSheet.LotQuantity,
        };
        return Partial("_MoveSheet", this);
    }

    /// <summary>Submits the Move (plantry-6owm) — full-lot transfer in place, or a partial-quantity
    /// split (rule 1); freeze/thaw is implicit from the destination (rule 2). Mirrors
    /// <see cref="OnPostAddStockAsync"/>'s shape: on success the refreshed <c>_StockDetail</c> fragment
    /// speaks for itself (new location, recomputed expiry) — no extra toast, matching Add stock/Set
    /// alert rather than Consume's shortfall-driven notice.</summary>
    public async Task<IActionResult> OnPostMoveAsync(Guid id)
    {
        ProductId = id;
        ClearOtherSheetValidation(nameof(MoveInput));

        if (MoveInput.EntryId is not { } entryId)
            return NotFound();

        if (!ModelState.IsValid)
            return await ReloadMoveSheetAsync(id, entryId);

        var result = await new TransferStockCommand(
            id, entryId, MoveInput.LocationId!.Value, MoveInput.Quantity!.Value,
            stocks, catalog, clock, tenant, transferLogger).ExecuteAsync();

        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await ReloadMoveSheetAsync(id, entryId);
        }

        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadChipsAsync(Detail);
        return Partial("_StockDetail", new StockDetailPartialModel(Detail, Oob: true, Notice: null, Chips));
    }

    private async Task<IActionResult> ReloadMoveSheetAsync(Guid id, Guid entryId)
    {
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        var moveSheet = await BuildMoveSheetAsync(id, entryId);
        if (moveSheet is null) return NotFound();
        MoveSheet = moveSheet;
        return Partial("_MoveSheet", this);
    }

    /// <summary>
    /// Builds the Move sheet's view model — the lot context strip's facts, the destination picker
    /// (frozen locations carry the ❄ suffix, the current location renders disabled), and the
    /// freeze/thaw candidate expiry + rule note <b>precomputed server-side</b>. Neither depends on the
    /// quantity or destination the shopper eventually picks — the client-side preview (Alpine, in
    /// <c>_MoveSheet.cshtml</c>) only ever switches between these two precomputed outcomes based on the
    /// live-selected destination's frozen-ness, so no date arithmetic needs to happen in JS.
    /// </summary>
    private async Task<MoveSheetViewModel?> BuildMoveSheetAsync(Guid productId, Guid entryId)
    {
        if (Detail is null) return null;
        var lot = Detail.Lots.FirstOrDefault(l => l.EntryId == StockEntryId.From(entryId));
        if (lot is null) return null;

        var product = await catalog.FindProductAsync(productId);
        var allLocations = await locations.ListActiveAsync();
        var today = Today();

        var sourceIsFrozen = allLocations.FirstOrDefault(l => l.Id == LocationId.From(lot.LocationId))?.IsFrozen ?? false;

        var destinations = allLocations
            .Select(l => new MoveDestinationOption(l.Id.Value, l.Name, l.IsFrozen, l.Id.Value == lot.LocationId))
            .ToList();

        // Pre-select the "opposite" storage type — the most likely reason to open Move is to freeze a
        // non-frozen lot or thaw a frozen one — falling back to any other location when the household
        // has only one storage type configured.
        var defaultDestinationId = destinations.FirstOrDefault(d => !d.IsCurrent && d.IsFrozen != sourceIsFrozen)?.Id
            ?? destinations.FirstOrDefault(d => !d.IsCurrent)?.Id
            ?? Guid.Empty;

        var freezeDays = product?.DefaultDueDaysAfterFreezing;
        var thawDays = product?.DefaultDueDaysAfterThawing;

        var freezeCandidateDisplay = freezeDays is { } fd ? today.AddDays(fd).ToString("d MMM yyyy") : null;
        var thawCandidateDisplay = thawDays is { } td ? today.AddDays(td).ToString("d MMM yyyy") : null;

        var freezeNote = freezeDays is { } fdn
            ? $"{fdn}-day after-freezing default from this product, counted from today."
            : "No after-freezing default is set for this product — the expiry is left as-is. FrozenAt is still recorded.";
        var thawNote = thawDays is { } tdn
            ? $"{tdn}-day after-thawing default from this product, counted from today."
            : "No after-thawing default is set for this product — the expiry is left as-is. ThawedAt is still recorded.";

        // Transition fact (UI spec §1): the fact that matches the lot's CURRENT storage type — a lot
        // sitting in a frozen location shows when it was frozen; one sitting non-frozen shows when it
        // was thawed. Either can be null (never transitioned).
        var transitionFact = sourceIsFrozen
            ? (lot.FrozenAt is { } frozenAt ? $"frozen {frozenAt.ToLocalTime():d MMM yyyy}" : null)
            : (lot.ThawedAt is { } thawedAt ? $"thawed {thawedAt.ToLocalTime():d MMM yyyy}" : null);

        return new MoveSheetViewModel(
            entryId,
            Detail.Name,
            lot.Quantity,
            lot.UnitCode,
            lot.LocationName ?? "—",
            sourceIsFrozen,
            lot.ExpiryDate is { } exp ? exp.ToString("d MMM yyyy") : "No expiry set",
            transitionFact,
            lot.ThawedAt is { } thawedAtDisplay ? thawedAtDisplay.ToLocalTime().ToString("d MMM yyyy") : null,
            freezeCandidateDisplay,
            freezeNote,
            thawCandidateDisplay,
            thawNote,
            destinations,
            defaultDestinationId);
    }

    public async Task<IActionResult> OnGetThresholdSheetAsync(Guid id)
    {
        ProductId = id;
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();

        ThresholdInput = new ThresholdInputModel { Threshold = Detail.LowStockThreshold };
        return Partial("_SetThresholdSheet", this);
    }

    public async Task<IActionResult> OnPostSetThresholdAsync(Guid id)
    {
        ProductId = id;
        ClearOtherSheetValidation(nameof(ThresholdInput));

        if (!ModelState.IsValid)
        {
            Detail = await queries.FindDetailAsync(id);
            if (Detail is null) return NotFound();
            return Partial("_SetThresholdSheet", this);
        }

        var result = await new SetLowStockThresholdCommand(
            id, ThresholdInput.Threshold, stocks, catalog, clock, tenant, thresholdLogger).ExecuteAsync();

        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            Detail = await queries.FindDetailAsync(id);
            if (Detail is null) return NotFound();
            return Partial("_SetThresholdSheet", this);
        }

        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadChipsAsync(Detail);
        return Partial("_StockDetail", new StockDetailPartialModel(Detail, Oob: true, Notice: null, Chips));
    }

    /// <summary>Opens the Add stock sheet (plantry-sjfn) — the zero-stock landing's primary CTA, also
    /// reachable any time a household wants to top up this exact product without going via Pantry.</summary>
    public async Task<IActionResult> OnGetAddStockSheetAsync(Guid id)
    {
        ProductId = id;
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();

        AddStockInput = new AddStockInputModel();
        await LoadUnitOptionsAsync();
        await LoadLocationOptionsAsync();
        return Partial("_AddStockSheet", this);
    }

    public async Task<IActionResult> OnPostAddStockAsync(Guid id)
    {
        ProductId = id;
        ClearOtherSheetValidation(nameof(AddStockInput));

        if (!ModelState.IsValid)
            return await ReloadAddStockSheetAsync(id);

        var expiry = AddStockInput.ExpiryDate ?? await catalogProducts.DefaultExpiryDateAsync(id);

        var cmd = new AddStockCommand(
            id, AddStockInput.Quantity!.Value, AddStockInput.UnitId!.Value, AddStockInput.LocationId!.Value,
            CurrentUserId, skuId: null, expiryDate: expiry, purchasedAt: Today(),
            stocks, catalog, clock, tenant, logger: addStockLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await ReloadAddStockSheetAsync(id);
        }

        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadChipsAsync(Detail);
        return Partial("_StockDetail", new StockDetailPartialModel(Detail, Oob: true, Notice: null, Chips));
    }

    public async Task<IActionResult> OnGetSetPriceSheetAsync(Guid id)
    {
        ProductId = id;
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();

        var product = await catalog.FindProductAsync(id);
        PriceInput = new PriceInputModel { Quantity = 1m, UnitId = product?.DefaultUnitId };
        await LoadUnitOptionsAsync();
        return Partial("_SetPriceSheet", this);
    }

    /// <summary>
    /// Records a household-entered price estimate (plantry-3fqm) — <see cref="PriceSource.Manual"/>,
    /// no store, no merchant text (design: manual entries carry no merchant provenance). On success closes
    /// the sheet and refreshes the price line via an out-of-band swap — mirrors the "single OOB fragment,
    /// empty sheet-host" idiom <see cref="OnPostSetThresholdAsync"/> uses for <c>_StockDetail</c>, except
    /// stock itself is untouched by a price change, so only the price display needs to refresh.
    /// </summary>
    public async Task<IActionResult> OnPostSetPriceAsync(Guid id)
    {
        ProductId = id;
        ClearOtherSheetValidation(nameof(PriceInput));

        if (!ModelState.IsValid)
        {
            Detail = await queries.FindDetailAsync(id);
            if (Detail is null) return NotFound();
            await LoadUnitOptionsAsync();
            return Partial("_SetPriceSheet", this);
        }

        var result = await new RecordObservationCommand(
            id, skuId: null, PriceInput.Price!.Value, PriceInput.Quantity!.Value, PriceInput.UnitId!.Value,
            merchantText: null, sourceRef: null, clock.UtcNow, CurrentUserId, PriceSource.Manual,
            priceRepository, priceCalculator, tenant, priceLogger).ExecuteAsync();

        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            Detail = await queries.FindDetailAsync(id);
            if (Detail is null) return NotFound();
            await LoadUnitOptionsAsync();
            return Partial("_SetPriceSheet", this);
        }

        var priceText = await BuildPriceDisplayAsync(id);
        return Partial("_PriceDisplay", new PriceDisplayPartialModel(priceText, Oob: true));
    }

    /// <summary>Resolves the current effective price the same way <c>CostingService</c> would
    /// (<see cref="PricingQueries.EffectivePriceAsync"/> — cheapest active deal, else latest
    /// purchase/manual observation), rendered as "£3.99 for 500 g". Null when nothing has ever
    /// been observed for this product.</summary>
    private async Task<string> BuildPriceDisplayAsync(Guid productId)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var observation = await pricingQueries.EffectivePriceAsync(productId, today);
        if (observation is null) return "No price recorded yet";

        var currency = await displayCurrency.GetAsync();
        var unitCodes = await catalog.GetUnitCodesAsync();
        var unitCode = unitCodes.GetValueOrDefault(observation.UnitId, "?");
        return $"{MoneyDisplay.Format(observation.Price, currency)} for {observation.Quantity.ToString("0.###")} {unitCode}";
    }

    /// <summary>
    /// The History grid's Source column (receipt-intake-history.md H11): a resolved Intake/Cook row (a
    /// journal id present in <paramref name="chips"/>) renders the provenance chip; everything else —
    /// Manual, an unresolved Intake/Cook row, or a legacy row with no <see cref="StockSourceType"/> at
    /// all — keeps the existing plain-text/muted fallback. Public and pure so it is unit-testable without
    /// a full page render (mirrors <c>Pantry.IndexModel.ExpiryCell</c>). <see cref="ProvenanceChip"/> carries
    /// only raw target ids (Plantry.Composition stays ASP.NET-free — plantry-72c6), so the caller supplies
    /// <paramref name="hrefFor"/> — built with <c>Url.Page</c> at the Razor-view call site, where an
    /// <c>IUrlHelper</c> is in scope — to turn a chip into its rendered href.
    /// </summary>
    internal static GridCell SourceCell(
        StockJournalRow row, IReadOnlyDictionary<Guid, ProvenanceChip> chips, Func<ProvenanceChip, string> hrefFor) =>
        chips.TryGetValue(row.JournalId, out var chip)
            ? GridCell.SourceChip(chip.Kind == ProvenanceChipKind.Cook ? SourceChipIcon.Cook : SourceChipIcon.Receipt, chip.Label, hrefFor(chip))
            : row.SourceType is { } src ? GridCell.Text(src.ToString()) : GridCell.Muted("—");

    /// <summary>
    /// Resolves a <see cref="ProvenanceChip"/> into its rendered href via <c>Url.Page</c> (PathBase-safe —
    /// plantry-72c6): the Intake chip links to the session detail page with a <c>#line-{id}</c> anchor to
    /// the committed line; the Cook chip links to the recipe detail page. Public so the Razor partial
    /// (<c>_StockDetail.cshtml</c>, the sole caller) can pass it straight to <see cref="SourceCell"/> as
    /// <c>chip => DetailModel.HrefFor(chip, Url)</c>.
    /// </summary>
    public static string HrefFor(ProvenanceChip chip, IUrlHelper url) => chip.Kind switch
    {
        ProvenanceChipKind.Intake => $"{url.Page("/Intake/Session", new { id = chip.TargetId })}#line-{chip.LineAnchorId}",
        ProvenanceChipKind.Cook => url.Page("/Recipes/Details", new { id = chip.TargetId })!,
        _ => throw new ArgumentOutOfRangeException(nameof(chip), chip.Kind, "Unknown provenance chip kind."),
    };

    /// <summary>
    /// Batch-resolves provenance chips (receipt-intake-history.md H4/H11) for the History rows sourced
    /// from Intake or Cook — Manual rows and legacy null-SourceType rows are never offered to the reader,
    /// since they always keep today's plain text regardless of what it returns.
    /// </summary>
    private async Task LoadChipsAsync(ProductStockDetail detail)
    {
        var candidates = detail.History
            .Where(h => h.SourceType is StockSourceType.Intake or StockSourceType.Cook)
            .Select(h => (h.JournalId, h.SourceType!.Value, h.SourceRef))
            .ToList();
        Chips = await provenance.ResolveAsync(candidates);
    }

    private Task<Plantry.SharedKernel.Result<ConsumeOutcome>> Run(
        Guid id, decimal amount, Guid unitId, StockReason reason, Guid? targetEntryId) =>
        new ConsumeStockCommand(
            id, amount, unitId, reason, CurrentUserId, targetEntryId, sourceRef: null,
            stocks, catalog, conversions, clock, tenant, logger: consumeLogger).ExecuteAsync();

    private async Task<IActionResult> ReloadSheetAsync(Guid id)
    {
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadUnitOptionsAsync();
        return Partial("_ConsumeSheet", this);
    }

    private async Task LoadUnitOptionsAsync()
    {
        UnitOptions = UnitSelectListBuilder.BuildFromUnits(
            await units.ListAsync(),
            u => u.Id.Value.ToString(),
            u => $"{u.Code} — {u.Name}");
    }

    private async Task LoadLocationOptionsAsync()
    {
        LocationOptions = (await locations.ListActiveAsync())
            .Select(l => new SelectListItem(l.Name, l.Id.Value.ToString()))
            .ToList();
    }

    private async Task<IActionResult> ReloadAddStockSheetAsync(Guid id)
    {
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadUnitOptionsAsync();
        await LoadLocationOptionsAsync();
        return Partial("_AddStockSheet", this);
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// This page carries five sibling <c>[BindProperty]</c> forms — <see cref="Input"/> (Consume),
    /// <see cref="ThresholdInput"/> (Set alert), <see cref="PriceInput"/> (Set price, plantry-3fqm),
    /// <see cref="AddStockInput"/> (Add stock, plantry-sjfn), and <see cref="MoveInput"/> (Move,
    /// plantry-6owm).
    /// Razor Pages binds and validates <b>every</b> <c>[BindProperty]</c> property on <i>every</i> POST
    /// regardless of which handler ran — a well-known cross-form gotcha — so a required field on a sheet
    /// the user never opened (e.g. <c>Input.Amount</c> while posting only the price sheet) would otherwise
    /// fail <see cref="PageModel.ModelState"/> even though that sheet's data was never posted. Worse: when
    /// the "Input.Amount" prefix has zero matches, <c>ComplexObjectModelBinder</c>'s empty-prefix fallback
    /// then also tries the <i>bare</i> field name ("Amount", "UnitId", …) — so the contaminating keys are
    /// not reliably prefixed and a plain <c>ModelState.ClearValidationState(nameof(Input))</c> misses them.
    /// Each POST handler calls this first, clearing every key that isn't this handler's own prefix, so the
    /// other four sheets' unrelated <c>[Required]</c> fields (prefixed or bare-fallback) can never
    /// contaminate this handler's <c>ModelState.IsValid</c>.
    /// </summary>
    private void ClearOtherSheetValidation(string keepPrefix)
    {
        // Remove(key) — not ClearValidationState(key) — is required here: ClearValidationState resets
        // an entry's state to Unvalidated rather than deleting it, and ModelState.IsValid treats any
        // remaining Unvalidated entry as not-valid too, so the contaminating keys would still block
        // IsValid even with no error message attached. Remove(key) drops the entry outright so it can
        // no longer affect the aggregate at all.
        var keysToClear = ModelState.Keys
            .Where(key => key != keepPrefix && !key.StartsWith(keepPrefix + ".", StringComparison.Ordinal))
            .ToList();
        foreach (var key in keysToClear)
            ModelState.Remove(key);
    }
}

/// <summary>View model for the lot/journal fragment; <see cref="Oob"/> drives the htmx out-of-band swap
/// after a consume from the sheet. <see cref="Chips"/> carries the resolved provenance chips
/// (receipt-intake-history.md H11) for the History grid's Source column, keyed by journal row id.</summary>
public sealed record StockDetailPartialModel(
    ProductStockDetail Detail, bool Oob, string? Notice,
    IReadOnlyDictionary<Guid, ProvenanceChip> Chips);

/// <summary>View model for the price-line fragment (plantry-3fqm). <see cref="Oob"/> drives the htmx
/// out-of-band swap after a "Set price" submission — mirrors <see cref="StockDetailPartialModel.Oob"/>'s
/// role for the lot/journal fragment, but scoped to just the price display since setting a price never
/// changes stock.</summary>
public sealed record PriceDisplayPartialModel(string DisplayText, bool Oob);

/// <summary>
/// View model for the header's primary action toggle (plantry-sjfn): "Consume" when the product has
/// active lots, "Add stock" on the zero-stock landing. Rendered once in the header itself
/// (<c>Oob: false</c>) and re-emitted out-of-band by <c>_StockDetail</c> wherever that partial already
/// opts into <c>Oob: true</c> (Consume, Add stock, Set alert) so the button stays correct without a
/// full reload — mirrors <see cref="StockDetailPartialModel.Oob"/>'s role for the subtitle. Discard
/// keeps its existing <c>Oob: false</c> behaviour (pre-existing; out of scope here), so discarding the
/// last lot to zero still needs a reload to flip the button — same pre-existing gap as the subtitle.
/// </summary>
public sealed record PrimaryStockActionPartialModel(Guid ProductId, bool HasStock, bool Oob);

/// <summary>One selectable destination in the Move sheet (plantry-6owm) — <see cref="IsCurrent"/>
/// renders it disabled and labelled "(current)"; <see cref="IsFrozen"/> renders the ❄ suffix (decided:
/// suffix, not a separate optgroup).</summary>
public sealed record MoveDestinationOption(Guid Id, string Name, bool IsFrozen, bool IsCurrent);

/// <summary>
/// View model for the Move sheet (plantry-6owm) — everything <c>_MoveSheet.cshtml</c>'s Alpine
/// component needs for its live split/effect preview, entirely precomputed server-side (see
/// <see cref="DetailModel.BuildMoveSheetAsync"/>): the freeze/thaw candidate expiry depends only on
/// "today" plus the product's resolved due-days default, neither of which changes as the shopper
/// adjusts quantity or destination, so no date arithmetic needs to happen in JS. A null
/// <see cref="FreezeCandidateDisplay"/>/<see cref="ThawCandidateDisplay"/> means no default is
/// configured (rule 6) — the sheet shows "(unchanged)" for that transition instead of a recomputed date.
/// </summary>
public sealed record MoveSheetViewModel(
    Guid EntryId,
    string ProductName,
    decimal LotQuantity,
    string UnitCode,
    string SourceLocationName,
    bool SourceIsFrozen,
    string ExistingExpiryDisplay,
    /// <summary>"frozen 2 Jun 2026" / "thawed 20 Jul 2026" — whichever matches the lot's current
    /// storage type; null if it has never transitioned.</summary>
    string? TransitionFactDisplay,
    /// <summary>Drives the refreeze warning (UI spec §5) — shown whenever the lot has EVER been
    /// thawed, regardless of <see cref="TransitionFactDisplay"/>'s recency.</summary>
    string? ThawedAtDisplay,
    string? FreezeCandidateDisplay,
    string FreezeNote,
    string? ThawCandidateDisplay,
    string ThawNote,
    IReadOnlyList<MoveDestinationOption> Destinations,
    Guid DefaultDestinationId);
