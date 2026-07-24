using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.MealPlanning.Application;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IMealPlanCatalogProductReader"/> — supplies the MealPlanning
/// context with catalog product existence checks over <see cref="CatalogDbContext"/>.
/// Lives in Plantry.Web (the composition root) to keep MealPlanning free of Catalog dependencies.
/// </summary>
public sealed class MealPlanCatalogProductReaderAdapter(CatalogDbContext db) : IMealPlanCatalogProductReader
{
    public async Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default)
    {
        // Compare the strongly-typed key, not p.Id.Value: EF can't translate a .Value access on a
        // converted value-object key when it's combined with the converted-key household query filter.
        var pid = ProductId.From(productId);
        return await db.Products.AnyAsync(
            p => p.Id == pid && p.ArchivedAt == null, ct);
    }

    public async Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default)
    {
        // A parent (HasVariants) product is not plannable as a direct product dish — no resolution
        // point ever asks "which variant was consumed" (plantry-pt79). Same key-comparison caveat as
        // ExistsAsync above.
        var pid = ProductId.From(productId);
        return await db.Products.AnyAsync(
            p => p.Id == pid && p.ArchivedAt == null && !p.HasVariants, ct);
    }

    public async Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(nameQuery) ? "" : nameQuery.Trim();

        // Parent (grouping) products are excluded (plantry-pt79) — only concrete products are
        // plannable as a direct product dish; their variants are ordinary leaf products and pass
        // through this filter unaffected.
        var products = await db.Products
            .Where(p => p.ArchivedAt == null && !p.HasVariants &&
                        (q == "" || EF.Functions.ILike(p.Name, $"%{q}%")))
            .OrderBy(p => p.Name)
            .Take(maxResults)
            .ToListAsync(ct);

        return products.Select(p => new MealPlanProductReadModel(p.Id.Value, p.Name)).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, string>();

        // Match on the strongly-typed key (same translation constraint as ExistsAsync).
        var ids = productIds.Select(ProductId.From).ToHashSet();
        var products = await db.Products
            .Where(p => ids.Contains(p.Id) && p.ArchivedAt == null)
            .ToListAsync(ct);

        return products.ToDictionary(p => p.Id.Value, p => p.Name);
    }
}
