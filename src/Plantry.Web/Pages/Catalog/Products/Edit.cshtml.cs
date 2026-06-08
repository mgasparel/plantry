using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Pages.Catalog.Products;

[Authorize]
public sealed class EditModel(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ProductId Id { get; private set; }
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];

    public sealed class InputModel
    {
        [Required, MaxLength(200)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Select a default unit.")]
        [Display(Name = "Default unit")]
        public Guid? DefaultUnitId { get; set; }

        [Display(Name = "Category")]
        public Guid? CategoryId { get; set; }

        [Display(Name = "Default location")]
        public Guid? DefaultLocationId { get; set; }

        [Range(0, 3650)]
        [Display(Name = "Default expiry (days)")]
        public int? DefaultDueDays { get; set; }

        [Range(0, 3650)]
        [Display(Name = "Default expiry after opening (days)")]
        public int? DefaultDueDaysAfterOpening { get; set; }

        [Range(0, 3650)]
        [Display(Name = "Default expiry after freezing (days)")]
        public int? DefaultDueDaysAfterFreezing { get; set; }

        [Range(0, 3650)]
        [Display(Name = "Default expiry after thawing (days)")]
        public int? DefaultDueDaysAfterThawing { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var product = await products.FindAsync(ProductId.From(id));
        if (product is null) return NotFound();

        Id = product.Id;
        Input = new InputModel
        {
            Name = product.Name,
            DefaultUnitId = product.DefaultUnitId.Value,
            CategoryId = product.CategoryId?.Value,
            DefaultLocationId = product.DefaultLocationId?.Value,
            DefaultDueDays = product.DefaultDueDays,
            DefaultDueDaysAfterOpening = product.DefaultDueDaysAfterOpening,
            DefaultDueDaysAfterFreezing = product.DefaultDueDaysAfterFreezing,
            DefaultDueDaysAfterThawing = product.DefaultDueDaysAfterThawing,
        };

        await LoadOptionsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Id = ProductId.From(id);

        if (!ModelState.IsValid)
        {
            await LoadOptionsAsync();
            return Page();
        }

        var cmd = new UpdateProductCommand(
            Id, Input.Name, Input.DefaultUnitId!.Value, Input.CategoryId, Input.DefaultLocationId,
            Input.DefaultDueDays, Input.DefaultDueDaysAfterOpening, Input.DefaultDueDaysAfterFreezing, Input.DefaultDueDaysAfterThawing,
            products, units, categories, locations, clock);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            if (result.Error == Plantry.SharedKernel.Error.NotFound) return NotFound();

            ModelState.AddModelError(string.Empty, result.Error.Description);
            await LoadOptionsAsync();
            return Page();
        }

        return RedirectToPage("Detail", new { id = id });
    }

    private async Task LoadOptionsAsync()
    {
        UnitOptions = (await units.ListAsync())
            .Select(u => new SelectListItem($"{u.Code} — {u.Name}", u.Id.Value.ToString()))
            .ToList();

        // Offer only active reference data, but keep this product's currently-assigned
        // category/location selectable even if it has since been archived — otherwise the
        // dropdown would silently re-point the product on save.
        var categoryOptions = (await categories.ListActiveAsync())
            .Select(c => new SelectListItem(c.Name, c.Id.Value.ToString()))
            .ToList();
        if (Input.CategoryId is { } categoryId && categoryOptions.All(o => o.Value != categoryId.ToString())
            && await categories.FindAsync(CategoryId.From(categoryId)) is { } category)
        {
            categoryOptions.Insert(0, new SelectListItem($"{category.Name} (archived)", category.Id.Value.ToString()));
        }
        CategoryOptions = categoryOptions;

        var locationOptions = (await locations.ListActiveAsync())
            .Select(l => new SelectListItem(l.Name, l.Id.Value.ToString()))
            .ToList();
        if (Input.DefaultLocationId is { } locationId && locationOptions.All(o => o.Value != locationId.ToString())
            && await locations.FindAsync(LocationId.From(locationId)) is { } location)
        {
            locationOptions.Insert(0, new SelectListItem($"{location.Name} (archived)", location.Id.Value.ToString()));
        }
        LocationOptions = locationOptions;
    }
}
