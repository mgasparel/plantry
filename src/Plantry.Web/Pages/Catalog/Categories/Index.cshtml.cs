using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Catalog.Categories;

[Authorize]
public sealed class IndexModel(
    ICategoryRepository categories,
    ITenantContext tenant,
    IClock clock,
    ILogger<CreateCategoryCommand> createCategoryLogger,
    ILogger<UpdateCategoryCommand> updateCategoryLogger) : PageModel
{
    public IReadOnlyList<Category> Categories { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required, MaxLength(100)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Range(0, 3650)]
        [Display(Name = "Default expiry (days)")]
        public int? DefaultDueDays { get; set; }

        [Display(Name = "Sort order")]
        public int SortOrder { get; set; }
    }

    public async Task OnGetAsync() => Categories = await categories.ListAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            Categories = await categories.ListActiveAsync();
            return Page();
        }

        var existing = await categories.ListActiveAsync();
        var nextSortOrder = existing.Count == 0 ? 0 : existing.Max(c => c.SortOrder) + 10;

        var cmd = new CreateCategoryCommand(Input.Name, Input.DefaultDueDays, nextSortOrder, categories, tenant, createCategoryLogger);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            Categories = await categories.ListActiveAsync();
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid id)
    {
        if (!ModelState.IsValid)
        {
            Categories = await categories.ListActiveAsync();
            return Page();
        }

        var cmd = new UpdateCategoryCommand(CategoryId.From(id), Input.Name, Input.DefaultDueDays, Input.SortOrder, categories, updateCategoryLogger);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            Categories = await categories.ListActiveAsync();
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReorderAsync(List<Guid> ids)
    {
        if (ids is null || ids.Count == 0) return BadRequest();

        var orderedIds = ids.Select(CategoryId.From).ToList();
        await new ReorderCategoriesCommand(orderedIds, categories).ExecuteAsync();
        return new OkResult();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var cmd = new ArchiveCategoryCommand(CategoryId.From(id), categories, clock);
        await cmd.ExecuteAsync();
        return RedirectToPage();
    }
}
