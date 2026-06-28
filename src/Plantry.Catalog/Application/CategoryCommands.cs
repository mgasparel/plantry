using Microsoft.Extensions.Logging;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Catalog.Application;

public sealed class CreateCategoryCommand(
    string name,
    int? defaultDueDays,
    int sortOrder,
    ICategoryRepository categories,
    ITenantContext tenant,
    ILogger<CreateCategoryCommand>? logger = null)
{
    public async Task<Result<CategoryId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (await categories.FindByNameAsync(name.Trim(), ct) is not null)
        {
            logger?.LogWarning("CreateCategory rejected — duplicate category name {CategoryName}.", name);
            return Error.Custom("Catalog.DuplicateCategoryName", $"A category named '{name}' already exists.");
        }

        var category = Category.Create(HouseholdId.From(householdId), name, defaultDueDays, sortOrder);
        await categories.AddAsync(category, ct);
        await categories.SaveChangesAsync(ct);

        logger?.LogInformation("Category {CategoryId} created with name {CategoryName}.", category.Id.Value, name);
        return category.Id;
    }
}

public sealed class UpdateCategoryCommand(
    CategoryId id,
    string name,
    int? defaultDueDays,
    int sortOrder,
    ICategoryRepository categories,
    ILogger<UpdateCategoryCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var category = await categories.FindAsync(id, ct);
        if (category is null)
        {
            logger?.LogWarning("UpdateCategory failed — category {CategoryId} not found.", id.Value);
            return Error.NotFound;
        }

        var existing = await categories.FindByNameAsync(name.Trim(), ct);
        if (existing is not null && existing.Id != id)
        {
            logger?.LogWarning("UpdateCategory {CategoryId} rejected — duplicate category name {CategoryName}.", id.Value, name);
            return Error.Custom("Catalog.DuplicateCategoryName", $"A category named '{name}' already exists.");
        }

        category.Rename(name);
        category.SetDefaultDueDays(defaultDueDays);
        category.SetSortOrder(sortOrder);
        await categories.SaveChangesAsync(ct);

        logger?.LogInformation("Category {CategoryId} updated.", id.Value);
        return Result.Success();
    }
}

/// <summary>
/// Applies a complete drag-and-drop ordering in one shot: the categories named by
/// <paramref name="orderedIds"/> are assigned sort orders in multiples of 10 in the given order.
/// Replaces the per-row reorder POST with a single batched call. Unknown/stale ids are skipped.
/// </summary>
public sealed class ReorderCategoriesCommand(IReadOnlyList<CategoryId> orderedIds, ICategoryRepository categories)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var byId = (await categories.ListActiveAsync(ct)).ToDictionary(c => c.Id);

        var sortOrder = 0;
        foreach (var id in orderedIds)
        {
            if (byId.TryGetValue(id, out var category))
            {
                category.SetSortOrder(sortOrder);
                sortOrder += 10;
            }
        }

        await categories.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Soft-deletes a category (Gate 6). Reference data is archived, never hard-deleted, so products
/// holding the (FK-less) <c>category_id</c> keep resolving to a name.
/// </summary>
public sealed class ArchiveCategoryCommand(CategoryId id, ICategoryRepository categories, IClock clock, ILogger<ArchiveCategoryCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var category = await categories.FindAsync(id, ct);
        if (category is null)
        {
            logger?.LogWarning("ArchiveCategory failed — category {CategoryId} not found.", id.Value);
            return Error.NotFound;
        }

        category.Archive(clock);
        await categories.SaveChangesAsync(ct);

        logger?.LogInformation("Category {CategoryId} archived.", id.Value);
        return Result.Success();
    }
}

public sealed class UnarchiveCategoryCommand(CategoryId id, ICategoryRepository categories, ILogger<UnarchiveCategoryCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var category = await categories.FindAsync(id, ct);
        if (category is null)
        {
            logger?.LogWarning("UnarchiveCategory failed — category {CategoryId} not found.", id.Value);
            return Error.NotFound;
        }

        category.Unarchive();
        await categories.SaveChangesAsync(ct);

        logger?.LogInformation("Category {CategoryId} unarchived.", id.Value);
        return Result.Success();
    }
}
