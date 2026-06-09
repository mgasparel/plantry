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

namespace Plantry.Web.Pages.Pantry.Products;

[Authorize]
public sealed class DetailModel(
    InventoryQueryService queries,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    ICatalogReadFacade catalog,
    IUnitRepository units,
    IClock clock,
    ITenantContext tenant) : PageModel
{
    public Guid ProductId { get; private set; }
    public ProductStockDetail? Detail { get; private set; }

    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    [BindProperty]
    public ConsumeInputModel Input { get; set; } = new();

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

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ProductId = id;
        Detail = await queries.FindDetailAsync(id);
        return Detail is null ? NotFound() : Page();
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
        return Partial("_StockDetail", new StockDetailPartialModel(Detail, Oob: true, Notice: notice));
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
        var notice = result.IsFailure ? result.Error.Description : null;
        return Partial("_StockDetail", new StockDetailPartialModel(Detail, Oob: false, Notice: notice));
    }

    private Task<Plantry.SharedKernel.Result<ConsumeOutcome>> Run(
        Guid id, decimal amount, Guid unitId, StockReason reason, Guid? targetEntryId) =>
        new ConsumeStockCommand(
            id, amount, unitId, reason, CurrentUserId, targetEntryId, sourceRef: null,
            stocks, conversions, clock, tenant).ExecuteAsync();

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
/// after a consume from the sheet.</summary>
public sealed record StockDetailPartialModel(ProductStockDetail Detail, bool Oob, string? Notice);
