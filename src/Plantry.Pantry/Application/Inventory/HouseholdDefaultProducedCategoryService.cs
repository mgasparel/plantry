using Microsoft.Extensions.Logging;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Pantry.Application;

public sealed class HouseholdDefaultProducedCategoryService(
    IHouseholdInventorySettingsRepository settings,
    ICategoryRepository categories,
    ITenantContext tenant,
    ILogger<HouseholdDefaultProducedCategoryService> logger) : IHouseholdDefaultProducedCategoryReader
{
    public async Task<Guid?> GetDefaultProducedCategoryIdAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } id) return null;
        var record = await settings.FindByHouseholdAsync(HouseholdId.From(id), ct);
        if (record?.DefaultProducedCategoryId is not { } categoryId) return null;
        var category = await categories.FindAsync(categoryId, ct);
        return category is { IsArchived: false } ? category.Id.Value : null;
    }

    public async Task<Result> ValidateAsync(Guid? categoryId, CancellationToken ct = default)
    {
        if (categoryId is not { } id) return Result.Success();
        var category = await categories.FindAsync(CategoryId.From(id), ct);
        if (category is null || category.IsArchived)
        {
            logger.LogWarning("Default produced category rejected — category {CategoryId} does not exist or is archived.", id);
            return Error.Custom("Inventory.UnknownDefaultProducedCategory", "Choose an active category.");
        }
        return Result.Success();
    }

    public async Task<Result> SetAsync(Guid? categoryId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid) return Error.Unauthorized;
        var validation = await ValidateAsync(categoryId, ct);
        if (validation.IsFailure) return validation;
        var householdId = HouseholdId.From(householdGuid);
        var record = await settings.FindByHouseholdAsync(householdId, ct);
        if (record is null)
        {
            record = HouseholdInventorySettings.Create(householdId);
            record.SetDefaultProducedCategoryId(categoryId is { } id ? CategoryId.From(id) : null);
            await settings.AddAsync(record, ct);
        }
        else record.SetDefaultProducedCategoryId(categoryId is { } id2 ? CategoryId.From(id2) : null);
        await settings.SaveChangesAsync(ct);
        return Result.Success();
    }
}
