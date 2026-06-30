using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="ICatalogWriter"/> — performs the inline Catalog mutations the
/// recipe author makes, over Catalog's own commands (no reimplementation): an untracked-staple create
/// (C12) via <see cref="CreateProductCommand"/> with <c>trackStock: false</c>; a standalone tracked-
/// product create (plantry-orix) with <c>trackStock: true</c>; group-aware creates via
/// <see cref="CreateVariantCommand"/> and <see cref="CreateGroupedProductCommand"/> (plantry-orix);
/// and a product-conversion add (C10) via <see cref="AddConversionCommand"/>. Throws on failure so
/// the calling author service can abort the save, mirroring the Intake create adapter.
///
/// <para>Recipes have no location concept, so none of the tracked-product paths accept a location
/// parameter — unlike <see cref="Plantry.Web.Inventory.TakeStockCatalogWriterAdapter"/> which always
/// sets a default location to the current walk location.</para>
/// </summary>
public sealed class CatalogWriterAdapter(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant) : ICatalogWriter
{
    public async Task<Guid> CreateUntrackedStapleAsync(string name, Guid defaultUnitId, CancellationToken ct = default)
    {
        var command = new CreateProductCommand(
            name, defaultUnitId, categoryId: null, defaultLocationId: null,
            products, units, categories, locations, clock, tenant, trackStock: false);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Create untracked staple failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }

    public async Task<Guid> CreateTrackedProductAsync(string name, Guid defaultUnitId, Guid? categoryId, CancellationToken ct = default)
    {
        var command = new CreateProductCommand(
            name, defaultUnitId, categoryId, defaultLocationId: null,
            products, units, categories, locations, clock, tenant, trackStock: true);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Create tracked product failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }

    public async Task<Guid> CreateTrackedVariantAsync(Guid parentGroupId, string variantName, Guid? unitOverride, Guid? categoryOverride, CancellationToken ct = default)
    {
        var command = new CreateVariantCommand(
            ProductId.From(parentGroupId), variantName,
            unitOverride, categoryOverride, locationOverride: null,
            products, units, categories, locations, clock, tenant);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Create tracked variant failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }

    public async Task<Guid> CreateTrackedGroupedProductAsync(string groupName, string variantName, Guid defaultUnitId, Guid? categoryId, CancellationToken ct = default)
    {
        var command = new CreateGroupedProductCommand(
            groupName, variantName, defaultUnitId, categoryId, defaultLocationId: null,
            products, units, categories, locations, clock, tenant);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Create tracked grouped product failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }

    public async Task AddConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default)
    {
        var command = new AddConversionCommand(
            ProductId.From(productId), fromUnitId, toUnitId, factor, products, units, clock);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Add product conversion failed ({result.Error.Code}): {result.Error.Description}");
    }
}
