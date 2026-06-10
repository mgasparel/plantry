using Plantry.Catalog.Domain;
using Plantry.Intake.Application;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="ICatalogHintProvider"/> — projects the household's active,
/// stock-eligible products (with their SKU labels) into <see cref="ProductHint"/>s for the receipt
/// parser. Reads Catalog directly so Plantry.Intake stays free of any Catalog dependency. Parent
/// products are excluded: a receipt line can never commit against one (it cannot hold stock).
/// </summary>
public sealed class CatalogHintProvider(IProductRepository products) : ICatalogHintProvider
{
    public async Task<IReadOnlyList<ProductHint>> GetHintsAsync(CancellationToken ct = default)
    {
        var active = await products.ListActiveAsync(ct);
        return active
            .Where(p => p.CanHoldStock)
            .Select(p => new ProductHint(
                p.Id.Value,
                p.Name,
                p.Skus.Select(s => s.Label).ToList()))
            .ToList();
    }
}
