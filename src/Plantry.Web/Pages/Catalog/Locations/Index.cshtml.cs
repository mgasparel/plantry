using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Catalog.Locations;

[Authorize]
public sealed class IndexModel(ILocationRepository locations, ITenantContext tenant, IClock clock) : PageModel
{
    public IReadOnlyList<Location> Locations { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required, MaxLength(100)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        public LocationType Type { get; set; }
    }

    public async Task OnGetAsync() => Locations = await locations.ListAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            Locations = await locations.ListActiveAsync();
            return Page();
        }

        var cmd = new CreateLocationCommand(Input.Name, Input.Type, locations, tenant);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            Locations = await locations.ListActiveAsync();
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var cmd = new ArchiveLocationCommand(LocationId.From(id), locations, clock);
        await cmd.ExecuteAsync();
        return RedirectToPage();
    }
}
