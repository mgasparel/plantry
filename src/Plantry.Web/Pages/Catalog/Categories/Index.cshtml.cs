using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
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

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Desc { get; set; }

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

    public static bool IsSortedRequest(string? sort) => sort?.ToLowerInvariant() is "name" or "name-desc" or "expiry" or "expiry-desc";

    public static IReadOnlyList<Category> SortCategories(IReadOnlyList<Category> listed, string? sort, bool descending)
    {
        var sortKey = sort?.ToLowerInvariant();
        if (sortKey is "name-desc" or "expiry-desc")
        {
            descending = true;
            sortKey = sortKey[..^5];
        }

        return sortKey switch
        {
            "name" when descending => listed.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.SortOrder).ToList(),
            "name" => listed.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.SortOrder).ToList(),
            "expiry" when descending => listed.OrderByDescending(c => c.DefaultDueDays ?? int.MaxValue).ThenBy(c => c.Name).ToList(),
            "expiry" => listed.OrderBy(c => c.DefaultDueDays ?? int.MaxValue).ThenBy(c => c.Name).ToList(),
            _ => listed
        };
    }

    public async Task OnGetAsync()
    {
        var listed = await categories.ListAsync();
        var sortKey = Sort?.ToLowerInvariant();
        Categories = SortCategories(listed, Sort, Desc);
        if (sortKey is "name-desc" or "expiry-desc")
        {
            Desc = true;
            Sort = sortKey[..^5];
        }
    }

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

        // Read the query string as well as the bound property so stale clients cannot
        // bypass the presentation-only guarantee if model binding is changed later.
        var requestedSort = Sort ?? Request.Query["sort"].ToString();
        if (IsSortedRequest(requestedSort)) return new ConflictResult();

        var orderedIds = ids.Select(CategoryId.From).ToList();

        // Only the manual-order view may persist a drag result. Sorted views are read-only projections.

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
