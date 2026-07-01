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
}
