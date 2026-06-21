using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Inventory;

/// <summary>
/// Web-side adapter for <see cref="ITakeStockCatalogWriter"/> (P4-7, J5, TS-8). Performs the two
/// inline Catalog mutations the Take Stock walk makes, over Catalog's own commands:
/// <list type="bullet">
///   <item>A tracked-product create (<c>trackStock: true</c>, with <c>defaultLocationId</c>) via
///   <see cref="CreateProductCommand"/> — mirrors <c>CatalogWriterAdapter.CreateUntrackedStapleAsync</c>
///   but with <c>trackStock: true</c> and a required <c>defaultLocationId</c> (C12).</item>
///   <item>A focused default-location set via <see cref="SetDefaultLocationCommand"/> (TS-9).</item>
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
        Guid defaultLocationId,
        CancellationToken ct = default)
    {
        var command = new CreateProductCommand(
            name, defaultUnitId, categoryId: null, defaultLocationId,
            products, units, categories, locations, clock, tenant, trackStock: true);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Create tracked product failed ({result.Error.Code}): {result.Error.Description}");

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
}
