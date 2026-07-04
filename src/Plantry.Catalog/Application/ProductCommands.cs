using Microsoft.Extensions.Logging;
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
    bool trackStock = true,
    ILogger<CreateProductCommand>? logger = null)
{
    public async Task<Result<ProductId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var crossRefError = await ValidateCrossReferencesAsync(defaultUnitId, categoryId, defaultLocationId, units, categories, locations, ct);
        if (crossRefError is not null)
        {
            logger?.LogWarning("CreateProduct rejected — cross-reference validation failed: {ErrorCode}.", crossRefError.Code);
            return crossRefError;
        }

        if (await products.FindByNameAsync(name.Trim(), ct) is not null)
        {
            logger?.LogWarning("CreateProduct rejected — duplicate product name {ProductName}.", name);
            return Error.Custom("Catalog.DuplicateProductName", $"A product named '{name}' already exists.");
        }

        var product = Product.Create(HouseholdId.From(householdId), name, UnitId.From(defaultUnitId), clock, trackStock);
        if (categoryId is { } catId) product.SetCategory(CategoryId.From(catId), clock);
        if (defaultLocationId is { } locId) product.SetDefaultLocation(LocationId.From(locId), clock);

        await products.AddAsync(product, ct);
        await products.SaveChangesAsync(ct);

        logger?.LogInformation("Product {ProductId} created with name {ProductName}.", product.Id.Value, name);
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
    IClock clock,
    ILogger<UpdateProductCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(id, ct);
        if (product is null)
        {
            logger?.LogWarning("UpdateProduct failed — product {ProductId} not found.", id.Value);
            return Error.NotFound;
        }

        var crossRefError = await CreateProductCommand.ValidateCrossReferencesAsync(
            defaultUnitId, categoryId, defaultLocationId, units, categories, locations, ct);
        if (crossRefError is not null)
        {
            logger?.LogWarning("UpdateProduct {ProductId} rejected — cross-reference validation failed: {ErrorCode}.", id.Value, crossRefError.Code);
            return crossRefError;
        }

        var existing = await products.FindByNameAsync(name.Trim(), ct);
        if (existing is not null && existing.Id != id)
        {
            logger?.LogWarning("UpdateProduct {ProductId} rejected — duplicate product name {ProductName}.", id.Value, name);
            return Error.Custom("Catalog.DuplicateProductName", $"A product named '{name}' already exists.");
        }

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
        logger?.LogInformation("Product {ProductId} updated.", id.Value);
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
    IClock clock,
    ILogger<AddSkuCommand>? logger = null)
{
    public async Task<Result<ProductSkuId>> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null)
        {
            logger?.LogWarning("AddSku failed — product {ProductId} not found.", productId.Value);
            return Error.NotFound;
        }

        if (sizeUnitId is { } unitId && await units.FindAsync(UnitId.From(unitId), ct) is null)
        {
            logger?.LogWarning("AddSku rejected for product {ProductId} — unknown size unit {UnitId}.", productId.Value, unitId);
            return Error.Custom("Catalog.UnknownUnit", "The selected size unit does not exist.");
        }

        var sku = product.AddSku(label, sizeQuantity, sizeUnitId is { } u ? UnitId.From(u) : null, clock);
        await products.SaveChangesAsync(ct);

        logger?.LogInformation("SKU {SkuId} added to product {ProductId}.", sku.Id.Value, productId.Value);
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
    IClock clock,
    ILogger<AddConversionCommand>? logger = null,
    ConversionSource source = ConversionSource.UserConfirmed)
{
    public async Task<Result<ProductConversionId>> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null)
        {
            logger?.LogWarning("AddConversion failed — product {ProductId} not found.", productId.Value);
            return Error.NotFound;
        }

        if (await units.FindAsync(UnitId.From(fromUnitId), ct) is null
            || await units.FindAsync(UnitId.From(toUnitId), ct) is null)
        {
            logger?.LogWarning("AddConversion rejected for product {ProductId} — one or both units not found ({FromUnitId}, {ToUnitId}).", productId.Value, fromUnitId, toUnitId);
            return Error.Custom("Catalog.UnknownUnit", "Both units must exist in this household.");
        }

        if (fromUnitId == toUnitId)
        {
            logger?.LogWarning("AddConversion rejected for product {ProductId} — from-unit and to-unit are the same ({UnitId}).", productId.Value, fromUnitId);
            return Error.Custom("Catalog.InvalidConversion", "A conversion's from-unit and to-unit must differ.");
        }

        var conversion = product.AddConversion(UnitId.From(fromUnitId), UnitId.From(toUnitId), factor, clock, source);

        if (product.IsParent)
        {
            foreach (var variant in await products.ListVariantsAsync(product.Id, ct))
                variant.InheritFrom(product, clock);
        }

        await products.SaveChangesAsync(ct);

        logger?.LogInformation("Conversion {ConversionId} ({ConversionSource}) added to product {ProductId}.", conversion.Id.Value, source, productId.Value);
        return conversion.Id;
    }
}

/// <summary>
/// Promotes an AI-suggested conversion to user-confirmed (ADR-022) through the Product aggregate
/// root. Idempotent — promoting an already-confirmed conversion succeeds without change.
/// </summary>
public sealed class PromoteConversionCommand(
    ProductId productId,
    ProductConversionId conversionId,
    IProductRepository products,
    IClock clock,
    ILogger<PromoteConversionCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null)
        {
            logger?.LogWarning("PromoteConversion failed — product {ProductId} not found.", productId.Value);
            return Error.NotFound;
        }

        try
        {
            product.PromoteConversion(conversionId, clock);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "PromoteConversion rejected — conversion {ConversionId} does not belong to product {ProductId}.", conversionId.Value, productId.Value);
            return Error.NotFound;
        }

        await products.SaveChangesAsync(ct);

        logger?.LogInformation("Conversion {ConversionId} promoted to user-confirmed on product {ProductId}.", conversionId.Value, productId.Value);
        return Result.Success();
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
/// Sets a product's default storage location without touching any other field (name, unit, category,
/// expiry defaults). Distinct from <see cref="UpdateProductCommand"/>, which field-clobs the whole
/// product and is not suited to a focused "assign a location" call-site (Take Stock P4-2, TS-9).
/// </summary>
public sealed class SetDefaultLocationCommand(
    ProductId productId,
    Guid locationId,
    IProductRepository products,
    ILocationRepository locations,
    IClock clock,
    ILogger<SetDefaultLocationCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null)
        {
            logger?.LogWarning("SetDefaultLocation failed — product {ProductId} not found.", productId.Value);
            return Error.NotFound;
        }

        if (await locations.FindAsync(LocationId.From(locationId), ct) is null)
        {
            logger?.LogWarning("SetDefaultLocation rejected for product {ProductId} — location {LocationId} not found.", productId.Value, locationId);
            return Error.Custom("Catalog.UnknownLocation", "The selected default location does not exist.");
        }

        product.SetDefaultLocation(LocationId.From(locationId), clock);
        await products.SaveChangesAsync(ct);

        logger?.LogInformation("Default location {LocationId} set for product {ProductId}.", locationId, productId.Value);
        return Result.Success();
    }
}

/// <summary>
/// Creates a new product as a variant of <paramref name="parentId"/> in a single atomic step,
/// fusing what used to be three manual steps (create → configure → MakeVariant) into one.
///
/// The parent must exist, must not itself be a variant (depth-1 invariant), and must belong
/// to the current tenant. All attributes not supplied by the caller are inherited from the parent
/// so the user only needs to provide the variant's distinguishing name.
///
/// The stock-hold block (AC: "Add a variant" blocked when standalone already holds stock) is
/// enforced by the caller (Detail page handler), which can cross into Inventory — this command
/// stays within the Catalog bounded context.
/// </summary>
public sealed class CreateVariantCommand(
    ProductId parentId,
    string name,
    Guid? unitOverride,
    Guid? categoryOverride,
    Guid? locationOverride,
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant,
    ILogger<CreateVariantCommand>? logger = null)
{
    public async Task<Result<ProductId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var parent = await products.FindAsync(parentId, ct);
        if (parent is null)
        {
            logger?.LogWarning("CreateVariant rejected — parent product {ParentId} not found.", parentId.Value);
            return Error.Custom("Catalog.UnknownParentProduct", "The selected parent product does not exist.");
        }

        if (parent.IsVariant)
        {
            logger?.LogWarning("CreateVariant rejected — parent {ParentId} is itself a variant (max depth 1).", parentId.Value);
            return Error.Custom("Catalog.MaxVariantDepthExceeded", "A variant cannot itself become a parent (max depth 1).");
        }

        // Resolve effective attribute IDs, falling back to parent's values.
        var effectiveUnitId = unitOverride ?? parent.DefaultUnitId.Value;
        var effectiveCategoryId = categoryOverride ?? parent.CategoryId?.Value;
        var effectiveLocationId = locationOverride ?? parent.DefaultLocationId?.Value;

        var crossRefError = await CreateProductCommand.ValidateCrossReferencesAsync(
            effectiveUnitId, effectiveCategoryId, effectiveLocationId, units, categories, locations, ct);
        if (crossRefError is not null)
        {
            logger?.LogWarning("CreateVariant rejected — cross-reference validation failed: {ErrorCode}.", crossRefError.Code);
            return crossRefError;
        }

        if (await products.FindByNameAsync(name.Trim(), ct) is not null)
        {
            logger?.LogWarning("CreateVariant rejected — duplicate product name {ProductName}.", name);
            return Error.Custom("Catalog.DuplicateProductName", $"A product named '{name}' already exists.");
        }

        var variant = Product.Create(HouseholdId.From(householdId), name, UnitId.From(effectiveUnitId), clock, trackStock: true);
        if (effectiveCategoryId is { } catId) variant.SetCategory(CategoryId.From(catId), clock);
        if (effectiveLocationId is { } locId) variant.SetDefaultLocation(LocationId.From(locId), clock);

        await products.AddAsync(variant, ct);
        variant.MakeVariantOf(parentId, clock);
        parent.SetHasVariants(true, clock);
        variant.InheritFrom(parent, clock);

        await products.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Variant product {VariantId} created and attached to parent {ParentId}.",
            variant.Id.Value, parentId.Value);
        return variant.Id;
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
    IClock clock,
    ILogger<MakeVariantCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null)
        {
            logger?.LogWarning("MakeVariant failed — product {ProductId} not found.", productId.Value);
            return Error.NotFound;
        }

        var parent = await products.FindAsync(parentId, ct);
        if (parent is null)
        {
            logger?.LogWarning("MakeVariant failed — parent product {ParentId} not found.", parentId.Value);
            return Error.Custom("Catalog.UnknownParentProduct", "The selected parent product does not exist.");
        }

        if (parent.IsVariant)
        {
            logger?.LogWarning("MakeVariant rejected — parent {ParentId} is itself a variant (max depth 1).", parentId.Value);
            return Error.Custom("Catalog.MaxVariantDepthExceeded", "A variant cannot itself become a parent (max depth 1).");
        }

        if (product.IsParent)
        {
            logger?.LogWarning("MakeVariant rejected — product {ProductId} is already a parent (max depth 1).", productId.Value);
            return Error.Custom("Catalog.MaxVariantDepthExceeded", "A parent product cannot itself become a variant (max depth 1).");
        }

        product.MakeVariantOf(parentId, clock);
        parent.SetHasVariants(true, clock);
        product.InheritFrom(parent, clock);

        await products.SaveChangesAsync(ct);
        logger?.LogInformation("Product {ProductId} attached as variant of {ParentId}.", productId.Value, parentId.Value);
        return Result.Success();
    }
}

/// <summary>
/// Creates a new group (abstract parent, <c>trackStock = false</c>) and its first variant
/// (the concrete product the user is actually adding) in a single atomic <c>SaveChanges</c>.
///
/// <para>This is the "hard case" from the quick-add group-creation prototype (plantry-40n6):
/// the user wants to create a product AND mint a new group at the same time, before any stock
/// lands. Because a stock-holding product can never be converted into a group afterwards, the
/// group must be born atomically alongside the first variant.</para>
///
/// <para>The group inherits the supplied unit/category/location and stores them as its defaults.
/// The variant inherits all defaults from the group (<see cref="Product.InheritFrom"/>), so a
/// single set of attribute fields covers both records.</para>
///
/// <para>Both the group name and the variant name must be unique within the household —
/// duplicate-name checks run for each before any write.</para>
///
/// <para>Returns the new <see cref="ProductId"/> of the <b>variant</b> (the stock-holding product
/// the caller wants to count/use).</para>
/// </summary>
public sealed class CreateGroupedProductCommand(
    string groupName,
    string variantName,
    Guid defaultUnitId,
    Guid? categoryId,
    Guid? defaultLocationId,
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant,
    ILogger<CreateGroupedProductCommand>? logger = null)
{
    public async Task<Result<ProductId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var crossRefError = await CreateProductCommand.ValidateCrossReferencesAsync(
            defaultUnitId, categoryId, defaultLocationId, units, categories, locations, ct);
        if (crossRefError is not null)
        {
            logger?.LogWarning("CreateGroupedProduct rejected — cross-reference validation failed: {ErrorCode}.", crossRefError.Code);
            return crossRefError;
        }

        // Both names must be free before we write anything.
        if (await products.FindByNameAsync(groupName.Trim(), ct) is not null)
        {
            logger?.LogWarning("CreateGroupedProduct rejected — duplicate group name {GroupName}.", groupName);
            return Error.Custom("Catalog.DuplicateProductName", $"A product named '{groupName}' already exists.");
        }

        if (await products.FindByNameAsync(variantName.Trim(), ct) is not null)
        {
            logger?.LogWarning("CreateGroupedProduct rejected — duplicate variant name {VariantName}.", variantName);
            return Error.Custom("Catalog.DuplicateProductName", $"A product named '{variantName}' already exists.");
        }

        // Mint the group — abstract parent, never holds stock.
        var group = Product.Create(HouseholdId.From(householdId), groupName, UnitId.From(defaultUnitId), clock, trackStock: false);
        if (categoryId is { } catId) group.SetCategory(CategoryId.From(catId), clock);
        if (defaultLocationId is { } locId) group.SetDefaultLocation(LocationId.From(locId), clock);

        // Mint the first variant — the concrete, stock-holding product.
        var variant = Product.Create(HouseholdId.From(householdId), variantName, UnitId.From(defaultUnitId), clock, trackStock: true);
        if (categoryId is { } catId2) variant.SetCategory(CategoryId.From(catId2), clock);
        if (defaultLocationId is { } locId2) variant.SetDefaultLocation(LocationId.From(locId2), clock);

        // Persist the group first (needed for the FK on MakeVariantOf).
        await products.AddAsync(group, ct);
        await products.AddAsync(variant, ct);

        // Link variant → group; set denormalized flag on group; inherit expiry + conversions.
        variant.MakeVariantOf(group.Id, clock);
        group.SetHasVariants(true, clock);
        variant.InheritFrom(group, clock);

        // Single SaveChanges — atomic creation of both records.
        await products.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Group {GroupId} ({GroupName}) created with first variant {VariantId} ({VariantName}).",
            group.Id.Value, groupName, variant.Id.Value, variantName);

        return variant.Id;
    }
}
