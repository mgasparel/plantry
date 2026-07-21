using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
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
    IStockProvenanceReader provenance,
    IClock clock,
    ITenantContext tenant,
    ILogger<ConsumeStockCommand> consumeLogger,
    ILogger<SetLowStockThresholdCommand> thresholdLogger) : PageModel
{
    public Guid ProductId { get; private set; }
    public ProductStockDetail? Detail { get; private set; }

    /// <summary>
    /// Resolved provenance chips (receipt-intake-history.md H11) for <see cref="Detail"/>'s History rows,
    /// keyed by <see cref="StockJournalRow.JournalId"/>. Only Intake/Cook rows are offered to the reader —
    /// Manual rows keep today's plain text unconditionally. A row absent from this dictionary (unresolved,
    /// or not attempted) renders its existing plain-text fallback.
    /// </summary>
    public IReadOnlyDictionary<Guid, ProvenanceChip> Chips { get; private set; } =
        new Dictionary<Guid, ProvenanceChip>();

    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    [BindProperty]
    public ConsumeInputModel Input { get; set; } = new();

    [BindProperty]
    public ThresholdInputModel ThresholdInput { get; set; } = new();

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

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ProductId = id;
        Detail = await queries.FindDetailAsync(id);
        if (Detail is null) return NotFound();
        await LoadChipsAsync(Detail);
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

    /// <summary>
    /// The History grid's Source column (receipt-intake-history.md H11): a resolved Intake/Cook row (a
    /// journal id present in <paramref name="chips"/>) renders the provenance chip; everything else —
    /// Manual, an unresolved Intake/Cook row, or a legacy row with no <see cref="StockSourceType"/> at
    /// all — keeps the existing plain-text/muted fallback. Public and pure so it is unit-testable without
    /// a full page render (mirrors <c>Pantry.IndexModel.ExpiryCell</c>).
    /// </summary>
    internal static GridCell SourceCell(StockJournalRow row, IReadOnlyDictionary<Guid, ProvenanceChip> chips) =>
        chips.TryGetValue(row.JournalId, out var chip)
            ? GridCell.SourceChip(chip.Kind == ProvenanceChipKind.Cook ? SourceChipIcon.Cook : SourceChipIcon.Receipt, chip.Label, chip.Href)
            : row.SourceType is { } src ? GridCell.Text(src.ToString()) : GridCell.Muted("—");

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

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

/// <summary>View model for the lot/journal fragment; <see cref="Oob"/> drives the htmx out-of-band swap
/// after a consume from the sheet. <see cref="Chips"/> carries the resolved provenance chips
/// (receipt-intake-history.md H11) for the History grid's Source column, keyed by journal row id.</summary>
public sealed record StockDetailPartialModel(
    ProductStockDetail Detail, bool Oob, string? Notice,
    IReadOnlyDictionary<Guid, ProvenanceChip> Chips);
