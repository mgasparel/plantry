using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Recipes;

/// <summary>
/// Web-side adapter for <see cref="ICatalogWriter"/> — performs the two inline Catalog mutations the
/// recipe author makes, over Catalog's own commands (no reimplementation): an untracked-staple create
/// (C12) via <see cref="CreateProductCommand"/> with <c>trackStock: false</c>, and a product-conversion
/// add (C10) via <see cref="AddConversionCommand"/>. Throws on failure so the calling author service can
/// abort the save, mirroring the Intake create adapter.
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

    public async Task AddConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default)
    {
        var command = new AddConversionCommand(
            ProductId.From(productId), fromUnitId, toUnitId, factor, products, units, clock);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Add product conversion failed ({result.Error.Code}): {result.Error.Description}");
    }
}
