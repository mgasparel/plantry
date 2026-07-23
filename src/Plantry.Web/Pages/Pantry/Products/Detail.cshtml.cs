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
    ILogger<AddStockCommand> addStockLogger) : PageModel
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

        var notice = result.Value.HasShortfall
            ? $"Consumed what was available — {result.Value.ShortfallAmount:0.###} could not be satisfied."
            : null;

        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadChipsAsync(Detail);
        return Partial("_StockDetail", new StockDetailPartialModel(Detail, Oob: true, Notice: notice, Chips));
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
            priceRepository, priceCalculator, tenant).ExecuteAsync();

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
        UnitOptions = (await units.ListAsync())
            .Select(u => new SelectListItem($"{u.Code} — {u.Name}", u.Id.Value.ToString()))
            .ToList();
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
    /// This page carries four sibling <c>[BindProperty]</c> forms — <see cref="Input"/> (Consume),
    /// <see cref="ThresholdInput"/> (Set alert), <see cref="PriceInput"/> (Set price, plantry-3fqm),
    /// and <see cref="AddStockInput"/> (Add stock, plantry-sjfn).
    /// Razor Pages binds and validates <b>every</b> <c>[BindProperty]</c> property on <i>every</i> POST
    /// regardless of which handler ran — a well-known cross-form gotcha — so a required field on a sheet
    /// the user never opened (e.g. <c>Input.Amount</c> while posting only the price sheet) would otherwise
    /// fail <see cref="PageModel.ModelState"/> even though that sheet's data was never posted. Worse: when
    /// the "Input.Amount" prefix has zero matches, <c>ComplexObjectModelBinder</c>'s empty-prefix fallback
    /// then also tries the <i>bare</i> field name ("Amount", "UnitId", …) — so the contaminating keys are
    /// not reliably prefixed and a plain <c>ModelState.ClearValidationState(nameof(Input))</c> misses them.
    /// Each POST handler calls this first, clearing every key that isn't this handler's own prefix, so the
    /// other two sheets' unrelated <c>[Required]</c> fields (prefixed or bare-fallback) can never
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
