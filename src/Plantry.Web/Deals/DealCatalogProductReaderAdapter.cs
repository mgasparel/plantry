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
public sealed class DealCatalogProductReaderAdapter(
    IProductRepository products, ICategoryRepository categories) : ICatalogProductReader
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

    public async Task<IReadOnlyDictionary<Guid, DealProductInfo>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, DealProductInfo>();

        // A confirmed deal's product may since have been archived; still resolve its name (like store
        // names, DM-16) so a past deal renders. Load the household's products/categories once, then join
        // in memory — categories carry no FK, so this is the FK-less resolve ICategoryRepository.ListAsync
        // exists for. This is a bounded page of deals, so no per-id round-trip.
        var wanted = productIds.ToHashSet();
        var all = await products.ListActiveAsync(ct);
        var matched = all.Where(p => wanted.Contains(p.Id.Value)).ToList();

        // Any id not among the active products (e.g. archived) — resolve individually so it still renders.
        foreach (var missing in wanted.Where(id => matched.All(p => p.Id.Value != id)))
        {
            var one = await products.FindAsync(ProductId.From(missing), ct);
            if (one is not null) matched.Add(one);
        }

        var categoryNames = (await categories.ListAsync(ct))
            .ToDictionary(c => c.Id, c => c.Name);

        return matched.ToDictionary(
            p => p.Id.Value,
            p => new DealProductInfo(
                p.Id.Value,
                p.Name,
                p.CategoryId is { } cid && categoryNames.TryGetValue(cid, out var name) ? name : null));
    }
}
