using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Pages.Catalog.Products;

[Authorize]
public sealed class DetailModel(
    IProductRepository products,
    IUnitRepository units,
    ProductQueryService queries,
    IClock clock) : PageModel
{
    public ProductId Id { get; private set; }
    public ProductDetail? Product { get; private set; }
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> ParentOptions { get; private set; } = [];

    public AddSkuInputModel SkuInput { get; set; } = new();

    public AddConversionInputModel ConversionInput { get; set; } = new();

    public MakeVariantInputModel VariantInput { get; set; } = new();

    public sealed class AddSkuInputModel
    {
        [Required, MaxLength(200)]
        [Display(Name = "Label")]
        public string Label { get; set; } = string.Empty;

        [Range(0.000001, double.MaxValue, ErrorMessage = "Size quantity must be positive when provided.")]
        [Display(Name = "Size quantity")]
        public decimal? SizeQuantity { get; set; }

        [Display(Name = "Size unit")]
        public Guid? SizeUnitId { get; set; }
    }

    public sealed class AddConversionInputModel
    {
        [Required(ErrorMessage = "Select a from-unit.")]
        [Display(Name = "From unit")]
        public Guid? FromUnitId { get; set; }

        [Required(ErrorMessage = "Select a to-unit.")]
        [Display(Name = "To unit")]
        public Guid? ToUnitId { get; set; }

        [Range(0.000001, double.MaxValue, ErrorMessage = "Factor must be positive.")]
        [Display(Name = "Factor")]
        public decimal Factor { get; set; }
    }

    public sealed class MakeVariantInputModel
    {
        [Required(ErrorMessage = "Select a parent product.")]
        [Display(Name = "Parent product")]
        public Guid? ParentProductId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Id = ProductId.From(id);
        Product = await queries.FindDetailAsync(Id);
        if (Product is null) return NotFound();

        await LoadOptionsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddSkuAsync(Guid id, [Bind(Prefix = "SkuInput")] AddSkuInputModel input)
    {
        Id = ProductId.From(id);
        SkuInput = input;
        if (!ModelState.IsValid) return await ReloadAsync();

        var cmd = new AddSkuCommand(Id, SkuInput.Label, SkuInput.SizeQuantity, SkuInput.SizeUnitId, products, units, clock);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure) return await ReloadWithErrorAsync(result.Error);

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveSkuAsync(Guid id, Guid skuId)
    {
        await new RemoveSkuCommand(ProductId.From(id), ProductSkuId.From(skuId), products, clock).ExecuteAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddConversionAsync(Guid id, [Bind(Prefix = "ConversionInput")] AddConversionInputModel input)
    {
        Id = ProductId.From(id);
        ConversionInput = input;
        if (!ModelState.IsValid) return await ReloadAsync();

        var cmd = new AddConversionCommand(
            Id, ConversionInput.FromUnitId!.Value, ConversionInput.ToUnitId!.Value, ConversionInput.Factor,
            products, units, clock);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure) return await ReloadWithErrorAsync(result.Error);

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveConversionAsync(Guid id, Guid conversionId)
    {
        await new RemoveConversionCommand(ProductId.From(id), ProductConversionId.From(conversionId), products, clock).ExecuteAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMakeVariantAsync(Guid id, [Bind(Prefix = "VariantInput")] MakeVariantInputModel input)
    {
        Id = ProductId.From(id);
        VariantInput = input;
        if (!ModelState.IsValid) return await ReloadAsync();

        var cmd = new MakeVariantCommand(Id, ProductId.From(VariantInput.ParentProductId!.Value), products, clock);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure) return await ReloadWithErrorAsync(result.Error);

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDetachAsync(Guid id)
    {
        await new DetachProductFromParentCommand(ProductId.From(id), products, clock).ExecuteAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id)
    {
        await new ArchiveProductCommand(ProductId.From(id), products, clock).ExecuteAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnarchiveAsync(Guid id)
    {
        await new UnarchiveProductCommand(ProductId.From(id), products, clock).ExecuteAsync();
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> ReloadWithErrorAsync(Error error)
    {
        ModelState.AddModelError(string.Empty, error.Description);
        return await ReloadAsync();
    }

    private async Task<IActionResult> ReloadAsync()
    {
        Product = await queries.FindDetailAsync(Id);
        if (Product is null) return NotFound();

        await LoadOptionsAsync();
        return Page();
    }

    private async Task LoadOptionsAsync()
    {
        UnitOptions = (await units.ListAsync())
            .Select(u => new SelectListItem($"{u.Code} — {u.Name}", u.Id.Value.ToString()))
            .ToList();

        var candidates = await products.ListActiveAsync();
        ParentOptions = candidates
            .Where(p => p.Id != Id && !p.IsVariant && !p.IsParent)
            .Select(p => new SelectListItem(p.Name, p.Id.Value.ToString()))
            .ToList();
    }
}
