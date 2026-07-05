using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Intake.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="ICreateProductPort"/> — creates a Catalog product on-the-fly during
/// intake commit, over <see cref="CreateProductCommand"/>. Throws on failure so the per-line commit can
/// abort that line without marking it committed (keeping the session resumable).
/// </summary>
public sealed class CreateProductAdapter(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant) : ICreateProductPort
{
    public async Task<Guid> CreateAsync(string name, Guid categoryId, Guid defaultUnitId, CancellationToken ct = default)
    {
        var command = new CreateProductCommand(
            name, defaultUnitId, categoryId, defaultLocationId: null,
            products, units, categories, locations, clock, tenant);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException($"Create product failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }
}
