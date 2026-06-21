using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// /Settings/Tags — household tag administration. Lists all tags (including archived), supports
/// create (name + optional category), rename, set-category, archive, and unarchive via htmx
/// fragment handlers. Each write returns the updated _TagsList partial so the list stays live
/// without a full page reload.
/// </summary>
[Authorize]
public sealed class TagsModel(ManageTagsService service) : PageModel
{
    public IReadOnlyList<TagViewModel> Tags { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public sealed record TagViewModel(
        TagId Id,
        string Name,
        TagCategory? Category,
        bool IsArchived);

    public async Task OnGetAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    // ── htmx fragment: return updated list ──────────────────────────────────────

    public async Task<IActionResult> OnGetListAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        return Partial("Settings/_TagsList", this);
    }

    // ── Create ──────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostCreateAsync(
        [FromForm] string name,
        [FromForm] string? category,
        CancellationToken ct = default)
    {
        var cat = ParseCategory(category);
        var result = await service.CreateAsync(name ?? string.Empty, cat, ct);
        if (!result.IsSuccess)
            ErrorMessage = result.Error;
        await LoadAsync(ct);
        return Partial("Settings/_TagsList", this);
    }

    // ── Rename ──────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostRenameAsync(
        [FromQuery] Guid id,
        [FromForm] string name,
        CancellationToken ct = default)
    {
        var result = await service.RenameAsync(TagId.From(id), name ?? string.Empty, ct);
        if (!result.IsSuccess)
            ErrorMessage = result.Error;
        await LoadAsync(ct);
        return Partial("Settings/_TagsList", this);
    }

    // ── Set category ────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostCategoryAsync(
        [FromQuery] Guid id,
        [FromForm] string? category,
        CancellationToken ct = default)
    {
        var cat = ParseCategory(category);
        var result = await service.SetCategoryAsync(TagId.From(id), cat, ct);
        if (!result.IsSuccess)
            ErrorMessage = result.Error;
        await LoadAsync(ct);
        return Partial("Settings/_TagsList", this);
    }

    // ── Archive ─────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostArchiveAsync(
        [FromQuery] Guid id,
        CancellationToken ct = default)
    {
        var result = await service.ArchiveAsync(TagId.From(id), ct);
        if (!result.IsSuccess)
            ErrorMessage = result.Error;
        await LoadAsync(ct);
        return Partial("Settings/_TagsList", this);
    }

    // ── Unarchive ───────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostUnarchiveAsync(
        [FromQuery] Guid id,
        CancellationToken ct = default)
    {
        var result = await service.UnarchiveAsync(TagId.From(id), ct);
        if (!result.IsSuccess)
            ErrorMessage = result.Error;
        await LoadAsync(ct);
        return Partial("Settings/_TagsList", this);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken ct)
    {
        var all = await service.ListAllAsync(ct);
        Tags = all.Select(t => new TagViewModel(t.Id, t.Name, t.Category, t.IsArchived)).ToList();
    }

    private static TagCategory? ParseCategory(string? value) =>
        value is null or "" ? null : Enum.TryParse<TagCategory>(value, out var cat) ? cat : null;
}
