using Plantry.Pantry.Domain;
using Plantry.Market.Application;

namespace Plantry.Web.Deals;

/// <summary>
/// Web-side adapter for the Deals <see cref="ICatalogProductReader"/> — validates that a deal's resolved
/// product is a live catalog product before <c>ConfirmDeal</c> commits it into memory + price history, over
/// Catalog's <see cref="IProductRepository"/>. Lives in Plantry.Web (the composition root that already
/// references both contexts) so <c>Plantry.Market</c> stays free of any Catalog dependency (ADR-010/DM-3),
/// mirroring <see cref="CatalogStoreReaderAdapter"/>. A deal may resolve to a parent product (plantry-oh27.6);
/// <see cref="ForProductsAsync"/> resolves its live variant ids so <c>ConfirmDeal</c> can fan the observation
/// out to each of them.
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
        // Active products, parents included (plantry-oh27.6): a deal may now resolve to a parent — the
        // fan-out to every live variant happens at confirm (ConfirmDeal), never here. The CanHoldStock
        // filter that used to exclude parents (mirroring Intake's CatalogHintProvider, which never
        // resolves a leaf-only ingredient line to a parent) is dropped for Deals specifically.
        var active = await products.ListActiveAsync(ct);
        return active
            .Select(p => new ProductCandidate(p.Id.Value, p.Name, IsParent: p.IsParent))
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

        // Live (non-archived) variants for every parent, grouped from the same active-products load
        // (plantry-oh27.6) — one query total, no per-parent ListVariantsAsync round trip.
        var variantsByParent = all
            .Where(p => p.ParentProductId is not null)
            .GroupBy(p => p.ParentProductId!.Value.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(p => p.Id.Value).ToList());

        // Any id not among the active products (e.g. archived) — resolve individually so it still renders.
        foreach (var missing in wanted.Where(id => matched.All(p => p.Id.Value != id)))
        {
            var one = await products.FindAsync(ProductId.From(missing), ct);
            if (one is not null) matched.Add(one);
        }

        var categoryNames = (await categories.ListAsync(ct))
            .ToDictionary(c => c.Id, c => c.Name);

        var result = new Dictionary<Guid, DealProductInfo>();
        foreach (var p in matched)
        {
            // An archived parent resolved via the individual fallback above never made it into the batch
            // grouping (that grouping only covers `all`, the active set) — fall back to a per-parent
            // ListVariantsAsync read for that rare path, filtered to still-live variants.
            IReadOnlyList<Guid> liveVariantIds = !p.IsParent
                ? []
                : variantsByParent.TryGetValue(p.Id.Value, out var grouped)
                    ? grouped
                    : (await products.ListVariantsAsync(p.Id, ct))
                        .Where(v => !v.IsArchived)
                        .Select(v => v.Id.Value)
                        .ToList();

            result[p.Id.Value] = new DealProductInfo(
                p.Id.Value,
                p.Name,
                p.CategoryId is { } cid && categoryNames.TryGetValue(cid, out var name) ? name : null,
                p.DefaultUnitId.Value,
                IsParent: p.IsParent,
                liveVariantIds: liveVariantIds);
        }
        return result;
    }
}
