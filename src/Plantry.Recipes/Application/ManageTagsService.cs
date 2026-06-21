using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that drives the tag administration flows in the /Settings area.
/// Enforces case-insensitive per-household name uniqueness on create and rename (R1).
/// Archived tags keep their name reserved so reuse is blocked while archived
/// (decision: archived name reserved, mirroring soft-delete convention).
/// </summary>
public sealed class ManageTagsService(
    ITagRepository tags,
    IClock clock,
    ITenantContext tenant)
{
    // ── Queries ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all tags (including archived) for the household, ordered by name.
    /// Used by the Settings/Tags admin page.
    /// </summary>
    public async Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct = default)
    {
        RequireHousehold();
        return await tags.ListAllAsync(activeOnly: false, ct);
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new active tag with the given name and optional category.
    /// Returns <see cref="ManageTagResult.Conflict"/> when the name is already in use
    /// (including by an archived tag).
    /// </summary>
    public async Task<ManageTagResult> CreateAsync(string name, TagCategory? category, CancellationToken ct = default)
    {
        var household = RequireHousehold();
        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return ManageTagResult.Invalid("Name must not be blank.");

        if (await tags.FindByNameAsync(household, trimmed, ct) is not null)
            return ManageTagResult.Conflict($"A tag named '{trimmed}' already exists.");

        var tag = Tag.Create(household, trimmed, category, clock);
        await tags.AddAsync(tag, ct);
        await tags.SaveChangesAsync(ct);
        return ManageTagResult.Ok(tag.Id);
    }

    /// <summary>
    /// Renames an existing tag.
    /// Returns <see cref="ManageTagResult.NotFound"/> when the tag does not exist.
    /// Returns <see cref="ManageTagResult.Conflict"/> when the new name is already in use by a
    /// different tag (including archived).
    /// </summary>
    public async Task<ManageTagResult> RenameAsync(TagId tagId, string name, CancellationToken ct = default)
    {
        var household = RequireHousehold();
        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return ManageTagResult.Invalid("Name must not be blank.");

        var tag = await tags.GetByIdAsync(tagId, ct);
        if (tag is null)
            return ManageTagResult.NotFound();

        // Uniqueness: only reject if another tag (not this one) owns the name.
        var existing = await tags.FindByNameAsync(household, trimmed, ct);
        if (existing is not null && existing.Id != tagId)
            return ManageTagResult.Conflict($"A tag named '{trimmed}' already exists.");

        tag.Rename(trimmed, clock);
        await tags.SaveChangesAsync(ct);
        return ManageTagResult.Ok(tagId);
    }

    /// <summary>Sets or clears the cosmetic category on a tag.</summary>
    public async Task<ManageTagResult> SetCategoryAsync(TagId tagId, TagCategory? category, CancellationToken ct = default)
    {
        var tag = await tags.GetByIdAsync(tagId, ct);
        if (tag is null)
            return ManageTagResult.NotFound();

        tag.SetCategory(category, clock);
        await tags.SaveChangesAsync(ct);
        return ManageTagResult.Ok(tagId);
    }

    /// <summary>
    /// Soft-deletes a tag: sets <c>ArchivedAt</c> and removes it from the active vocabulary.
    /// Existing recipe references survive and still resolve their display names.
    /// Idempotent.
    /// </summary>
    public async Task<ManageTagResult> ArchiveAsync(TagId tagId, CancellationToken ct = default)
    {
        var tag = await tags.GetByIdAsync(tagId, ct);
        if (tag is null)
            return ManageTagResult.NotFound();

        tag.Archive(clock);
        await tags.SaveChangesAsync(ct);
        return ManageTagResult.Ok(tagId);
    }

    /// <summary>
    /// Restores an archived tag to the active vocabulary.
    /// Idempotent.
    /// </summary>
    public async Task<ManageTagResult> UnarchiveAsync(TagId tagId, CancellationToken ct = default)
    {
        var tag = await tags.GetByIdAsync(tagId, ct);
        if (tag is null)
            return ManageTagResult.NotFound();

        tag.Unarchive(clock);
        await tags.SaveChangesAsync(ct);
        return ManageTagResult.Ok(tagId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private HouseholdId RequireHousehold()
    {
        if (tenant.HouseholdId is not { } id)
            throw new InvalidOperationException("No authenticated household.");
        return HouseholdId.From(id);
    }
}

// ── Result ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The outcome of a tag management write operation.
/// </summary>
public sealed record ManageTagResult
{
    private ManageTagResult() { }

    public bool IsSuccess { get; init; }
    public TagId? TagId { get; init; }
    public string? Error { get; init; }

    public static ManageTagResult Ok(TagId tagId) => new() { IsSuccess = true, TagId = tagId };
    public static ManageTagResult Conflict(string message) => new() { IsSuccess = false, Error = message };
    public static ManageTagResult Invalid(string message) => new() { IsSuccess = false, Error = message };
    public static ManageTagResult NotFound() => new() { IsSuccess = false, Error = "Tag not found." };
}
