using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Inventory;

/// <summary>
/// Web-side adapter for <see cref="ITakeStockCatalogWriter"/> (P4-7, J5, TS-8). Performs the
/// inline Catalog mutations the Take Stock walk makes, over Catalog's own commands:
/// <list type="bullet">
///   <item>Standalone tracked-product create (<c>trackStock: true</c>, with <c>defaultLocationId</c>)
///   via <see cref="CreateProductCommand"/> — mirrors <c>CatalogWriterAdapter.CreateUntrackedStapleAsync</c>
///   but with <c>trackStock: true</c> and a required <c>defaultLocationId</c> (C12).</item>
///   <item>Tracked variant create (join existing group) via <see cref="CreateVariantCommand"/>
///   — inherits unit/category/location from parent unless overridden (plantry-l92u).</item>
///   <item>Tracked grouped-product create (new group + first variant) via
///   <see cref="CreateGroupedProductCommand"/> — atomic two-record create (plantry-l92u).</item>
///   <item>Focused default-location set via <see cref="SetDefaultLocationCommand"/> (TS-9).</item>
/// </list>
/// Throws <see cref="InvalidOperationException"/> on Catalog failure so the <see cref="AddCountedItemCommand"/>
/// can surface the error inline — same pattern as <c>CatalogWriterAdapter</c>.
/// </summary>
public sealed class TakeStockCatalogWriterAdapter(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant) : ITakeStockCatalogWriter
{
    public async Task<Guid> CreateTrackedProductAsync(
        string name,
        Guid defaultUnitId,
        Guid? categoryId,
        Guid defaultLocationId,
        CancellationToken ct = default)
    {
        var command = new CreateProductCommand(
            name, defaultUnitId, categoryId, defaultLocationId,
            products, units, categories, locations, clock, tenant, trackStock: true);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Create tracked product failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }

    public async Task<Guid> CreateTrackedVariantAsync(
        Guid parentGroupId,
        string variantName,
        Guid? unitOverride,
        Guid? categoryOverride,
        Guid? locationOverride,
        CancellationToken ct = default)
    {
        var command = new CreateVariantCommand(
            ProductId.From(parentGroupId),
            variantName,
            unitOverride,
            categoryOverride,
            locationOverride,
            products, units, categories, locations, clock, tenant);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Create tracked variant failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }

    public async Task<Guid> CreateTrackedGroupedProductAsync(
        string groupName,
        string variantName,
        Guid defaultUnitId,
        Guid? categoryId,
        Guid? defaultLocationId,
        CancellationToken ct = default)
    {
        var command = new CreateGroupedProductCommand(
            groupName, variantName, defaultUnitId, categoryId, defaultLocationId,
            products, units, categories, locations, clock, tenant);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Create grouped product failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }

    public async Task SetDefaultLocationAsync(
        Guid productId,
        Guid locationId,
        CancellationToken ct = default)
    {
        var command = new SetDefaultLocationCommand(
            ProductId.From(productId), locationId, products, locations, clock);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Set default location failed ({result.Error.Code}): {result.Error.Description}");
    }

    public async Task AddConversionAsync(
        Guid productId,
        Guid fromUnitId,
        Guid toUnitId,
        decimal factor,
        CancellationToken ct = default)
    {
        var command = new AddConversionCommand(
            ProductId.From(productId), fromUnitId, toUnitId, factor, products, units, clock);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Add product conversion failed ({result.Error.Code}): {result.Error.Description}");
    }
}
