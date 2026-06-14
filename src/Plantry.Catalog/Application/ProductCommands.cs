using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Catalog.Application;

public sealed class CreateProductCommand(
    string name,
    Guid defaultUnitId,
    Guid? categoryId,
    Guid? defaultLocationId,
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant,
    bool trackStock = true)
{
    public async Task<Result<ProductId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var crossRefError = await ValidateCrossReferencesAsync(defaultUnitId, categoryId, defaultLocationId, units, categories, locations, ct);
        if (crossRefError is not null)
            return crossRefError;

        if (await products.FindByNameAsync(name.Trim(), ct) is not null)
            return Error.Custom("Catalog.DuplicateProductName", $"A product named '{name}' already exists.");

        var product = Product.Create(HouseholdId.From(householdId), name, UnitId.From(defaultUnitId), clock, trackStock);
        if (categoryId is { } catId) product.SetCategory(CategoryId.From(catId), clock);
        if (defaultLocationId is { } locId) product.SetDefaultLocation(LocationId.From(locId), clock);

        await products.AddAsync(product, ct);
        await products.SaveChangesAsync(ct);

        return product.Id;
    }

    internal static async Task<Error?> ValidateCrossReferencesAsync(
        Guid defaultUnitId, Guid? categoryId, Guid? defaultLocationId,
        IUnitRepository units, ICategoryRepository categories, ILocationRepository locations,
        CancellationToken ct)
    {
        if (await units.FindAsync(UnitId.From(defaultUnitId), ct) is null)
            return Error.Custom("Catalog.UnknownUnit", "The selected default unit does not exist.");

        if (categoryId is { } catId && await categories.FindAsync(CategoryId.From(catId), ct) is null)
            return Error.Custom("Catalog.UnknownCategory", "The selected category does not exist.");

        if (defaultLocationId is { } locId && await locations.FindAsync(LocationId.From(locId), ct) is null)
            return Error.Custom("Catalog.UnknownLocation", "The selected default location does not exist.");

        return null;
    }
}

public sealed class UpdateProductCommand(
    ProductId id,
    string name,
    Guid defaultUnitId,
    Guid? categoryId,
    Guid? defaultLocationId,
    int? defaultDueDays,
    int? defaultDueDaysAfterOpening,
    int? defaultDueDaysAfterFreezing,
    int? defaultDueDaysAfterThawing,
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(id, ct);
        if (product is null) return Error.NotFound;

        var crossRefError = await CreateProductCommand.ValidateCrossReferencesAsync(
            defaultUnitId, categoryId, defaultLocationId, units, categories, locations, ct);
        if (crossRefError is not null)
            return crossRefError;

        var existing = await products.FindByNameAsync(name.Trim(), ct);
        if (existing is not null && existing.Id != id)
            return Error.Custom("Catalog.DuplicateProductName", $"A product named '{name}' already exists.");

        product.Rename(name, clock);
        product.SetDefaultUnit(UnitId.From(defaultUnitId), clock);
        product.SetCategory(categoryId is { } catId ? CategoryId.From(catId) : null, clock);
        product.SetDefaultLocation(defaultLocationId is { } locId ? LocationId.From(locId) : null, clock);
        product.SetExpiryDefaults(defaultDueDays, defaultDueDaysAfterOpening, defaultDueDaysAfterFreezing, defaultDueDaysAfterThawing, clock);

        if (product.IsParent)
        {
            foreach (var variant in await products.ListVariantsAsync(product.Id, ct))
                variant.InheritFrom(product, clock);
        }

        await products.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ArchiveProductCommand(ProductId id, IProductRepository products, IClock clock)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(id, ct);
        if (product is null) return Error.NotFound;

        product.Archive(clock);
        await products.SaveChangesAsync(ct);

        return Result.Success();
    }
}

public sealed class UnarchiveProductCommand(ProductId id, IProductRepository products, IClock clock)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(id, ct);
        if (product is null) return Error.NotFound;

        product.Unarchive(clock);
        await products.SaveChangesAsync(ct);

        return Result.Success();
    }
}

public sealed class AddSkuCommand(
    ProductId productId,
    string label,
    decimal? sizeQuantity,
    Guid? sizeUnitId,
    IProductRepository products,
    IUnitRepository units,
    IClock clock)
{
    public async Task<Result<ProductSkuId>> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null) return Error.NotFound;

        if (sizeUnitId is { } unitId && await units.FindAsync(UnitId.From(unitId), ct) is null)
            return Error.Custom("Catalog.UnknownUnit", "The selected size unit does not exist.");

        var sku = product.AddSku(label, sizeQuantity, sizeUnitId is { } u ? UnitId.From(u) : null, clock);
        await products.SaveChangesAsync(ct);

        return sku.Id;
    }
}

public sealed class RemoveSkuCommand(ProductId productId, ProductSkuId skuId, IProductRepository products, IClock clock)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null) return Error.NotFound;

        product.RemoveSku(skuId, clock);
        await products.SaveChangesAsync(ct);

        return Result.Success();
    }
}

public sealed class AddConversionCommand(
    ProductId productId,
    Guid fromUnitId,
    Guid toUnitId,
    decimal factor,
    IProductRepository products,
    IUnitRepository units,
    IClock clock)
{
    public async Task<Result<ProductConversionId>> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null) return Error.NotFound;

        if (await units.FindAsync(UnitId.From(fromUnitId), ct) is null
            || await units.FindAsync(UnitId.From(toUnitId), ct) is null)
        {
            return Error.Custom("Catalog.UnknownUnit", "Both units must exist in this household.");
        }

        if (fromUnitId == toUnitId)
            return Error.Custom("Catalog.InvalidConversion", "A conversion's from-unit and to-unit must differ.");

        var conversion = product.AddConversion(UnitId.From(fromUnitId), UnitId.From(toUnitId), factor, clock);

        if (product.IsParent)
        {
            foreach (var variant in await products.ListVariantsAsync(product.Id, ct))
                variant.InheritFrom(product, clock);
        }

        await products.SaveChangesAsync(ct);

        return conversion.Id;
    }
}

public sealed class RemoveConversionCommand(ProductId productId, ProductConversionId conversionId, IProductRepository products, IClock clock)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null) return Error.NotFound;

        product.RemoveConversion(conversionId, clock);
        await products.SaveChangesAsync(ct);

        return Result.Success();
    }
}

/// <summary>
/// Detaches a variant from its parent, re-deriving the parent's denormalized
/// <see cref="Product.HasVariants"/> flag from the remaining siblings (archived included) —
/// another cross-aggregate consistency duty that has to live in the application layer.
/// </summary>
public sealed class DetachProductFromParentCommand(ProductId productId, IProductRepository products, IClock clock)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null) return Error.NotFound;

        if (product.ParentProductId is not { } parentId)
            return Result.Success();

        var parent = await products.FindAsync(parentId, ct);
        product.DetachFromParent(clock);

        if (parent is not null)
        {
            // Count every remaining variant, archived included — an archived variant still points
            // at this parent, so the parent stays a parent until that variant is detached too.
            var siblings = await products.ListVariantsAsync(parent.Id, ct);
            var stillHasVariants = siblings.Any(p => p.Id != product.Id);
            if (!stillHasVariants)
                parent.SetHasVariants(false, clock);
        }

        await products.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Attaches <paramref name="productId"/> as a variant of <paramref name="parentId"/>, enforcing
/// the depth-1 invariant (catalog.md "max depth 1") — a check that needs both aggregates loaded,
/// so it cannot live on <see cref="Product"/> alone.
/// </summary>
public sealed class MakeVariantCommand(
    ProductId productId,
    ProductId parentId,
    IProductRepository products,
    IClock clock)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null) return Error.NotFound;

        var parent = await products.FindAsync(parentId, ct);
        if (parent is null)
            return Error.Custom("Catalog.UnknownParentProduct", "The selected parent product does not exist.");

        if (parent.IsVariant)
            return Error.Custom("Catalog.MaxVariantDepthExceeded", "A variant cannot itself become a parent (max depth 1).");

        if (product.IsParent)
            return Error.Custom("Catalog.MaxVariantDepthExceeded", "A parent product cannot itself become a variant (max depth 1).");

        product.MakeVariantOf(parentId, clock);
        parent.SetHasVariants(true, clock);
        product.InheritFrom(parent, clock);

        await products.SaveChangesAsync(ct);
        return Result.Success();
    }
}
