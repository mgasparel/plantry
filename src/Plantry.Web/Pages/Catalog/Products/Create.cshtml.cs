using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Catalog.Products;

[Authorize]
public sealed class CreateModel(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant,
    ILogger<CreateProductCommand> createProductLogger) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

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

        /// <summary>
        /// Whether this product participates in quantity accounting (Product.TrackStock). Defaults
        /// checked — the untracked-staple path stays a deliberate opt-out, not the default.
        /// </summary>
        [Display(Name = "Track stock")]
        public bool TrackStock { get; set; } = true;
    }

    public async Task OnGetAsync() => await LoadOptionsAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadOptionsAsync();
            return Page();
        }

        var cmd = new CreateProductCommand(
            Input.Name, Input.DefaultUnitId!.Value, Input.CategoryId, Input.DefaultLocationId,
            products, units, categories, locations, clock, tenant,
            trackStock: Input.TrackStock, logger: createProductLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            await LoadOptionsAsync();
            return Page();
        }

        return RedirectToPage("Detail", new { id = result.Value.Value });
    }

    private async Task LoadOptionsAsync()
    {
        UnitOptions = (await units.ListAsync())
            .Select(u => new SelectListItem($"{u.Code} — {u.Name}", u.Id.Value.ToString()))
            .ToList();
        CategoryOptions = (await categories.ListActiveAsync())
            .Select(c => new SelectListItem(c.Name, c.Id.Value.ToString()))
            .ToList();
        LocationOptions = (await locations.ListActiveAsync())
            .Select(l => new SelectListItem(l.Name, l.Id.Value.ToString()))
            .ToList();
    }
}
