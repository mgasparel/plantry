using Plantry.Catalog.Domain;
using Plantry.Intake.Application;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="ICatalogHintProvider"/> — projects the household's active,
/// stock-eligible products (with their SKU labels) into <see cref="ProductHint"/>s for the receipt
/// parser. Reads Catalog directly so Plantry.Intake stays free of any Catalog dependency. Parent
/// products are excluded: a receipt line can never commit against one (it cannot hold stock).
/// </summary>
public sealed class CatalogHintProvider(IProductRepository products, IUnitRepository units) : ICatalogHintProvider
{
    public async Task<IReadOnlyList<ProductHint>> GetHintsAsync(CancellationToken ct = default)
    {
        var active = await products.ListActiveAsync(ct);
        var allUnits = await units.ListAsync(ct);
        // Each-tracked = the product's default stocking unit is a Count/each unit. Drives whether the
        // parser may estimate an each-count for a weight-priced match (plantry-1mu).
        var countUnitIds = allUnits
            .Where(u => u.Dimension == Dimension.Count)
            .Select(u => u.Id)
            .ToHashSet();

        return active
            .Where(p => p.CanHoldStock)
            .Select(p => new ProductHint(
                p.Id.Value,
                p.Name,
                p.Skus.Select(s => s.Label).ToList(),
                TracksEach: countUnitIds.Contains(p.DefaultUnitId)))
            .ToList();
    }
}
