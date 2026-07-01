using Plantry.Catalog.Domain;
using Plantry.Deals.Application;

namespace Plantry.Web.Deals;

/// <summary>
/// Web-side adapter for the Deals <see cref="ICatalogProductReader"/> — validates that a deal's resolved
/// product is a live catalog product before <c>ConfirmDeal</c> commits it into memory + price history, over
/// Catalog's <see cref="IProductRepository"/>. Lives in Plantry.Web (the composition root that already
/// references both contexts) so <c>Plantry.Deals</c> stays free of any Catalog dependency (ADR-010/DM-3),
/// mirroring <see cref="CatalogStoreReaderAdapter"/>.
/// </summary>
public sealed class DealCatalogProductReaderAdapter(IProductRepository products) : ICatalogProductReader
{
    public async Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await products.FindAsync(ProductId.From(productId), ct);
        return product is not null && !product.IsArchived;
    }

    public async Task<IReadOnlyList<ProductCandidate>> ListCandidatesAsync(CancellationToken ct = default)
    {
        // Active, stock-eligible products only: a deal can never resolve to a parent product (it holds no
        // stock and carries no price), mirroring Intake's CatalogHintProvider.
        var active = await products.ListActiveAsync(ct);
        return active
            .Where(p => p.CanHoldStock)
            .Select(p => new ProductCandidate(p.Id.Value, p.Name))
            .ToList();
    }
}
