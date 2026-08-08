using Microsoft.EntityFrameworkCore;
using Plantry.Market.Domain;

namespace Plantry.Market.Infrastructure;

public sealed class PriceObservationRepository(MarketDbContext db) : IPriceObservationRepository
{
    public async Task AddAsync(PriceObservation observation, CancellationToken ct = default) =>
        await db.PriceObservations.AddAsync(observation, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    // Deliberately NOT superseded-filtered — the caller needs the row back even if it is already
    // superseded, so PriceObservation.Supersede can throw its own guard (ADR-023 A7).
    public Task<PriceObservation?> FindAsync(PriceObservationId id, CancellationToken ct = default) =>
        db.PriceObservations.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) =>
        await db.PriceObservations
            .Where(p => p.Source == PriceSource.Purchase
                && p.StoreId == null
                && p.MerchantText != null
                && p.MerchantText.Trim() != ""
                && p.SupersededById == null)
            .OrderBy(p => p.ObservedAt)
            .ToListAsync(ct);

    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.ProductId == productId
                && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                && p.SupersededById == null)
            .OrderByDescending(p => p.ObservedAt)
            .FirstOrDefaultAsync(ct);

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.SkuId == skuId
                && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                && p.SupersededById == null)
            .OrderByDescending(p => p.ObservedAt)
            .FirstOrDefaultAsync(ct);

    public Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.ProductId == productId
                && p.Source == PriceSource.Deal
                && p.ValidFrom <= today
                && p.ValidTo >= today
                && p.SupersededById == null)
            .OrderBy(p => p.UnitPrice)
            .ThenBy(p => p.Price)
            .FirstOrDefaultAsync(ct);

    public Task<PriceObservation?> ActiveDealForPurchaseAsync(Guid productId, Guid storeId, DateOnly observedDate, decimal purchaseUnitPrice, decimal tolerance, CancellationToken ct = default) =>
        db.PriceObservations
            .Where(p => p.ProductId == productId
                && p.Source == PriceSource.Deal
                && p.StoreId == storeId
                && p.ValidFrom <= observedDate
                && p.ValidTo >= observedDate
                && p.SupersededById == null
                // Qualification lives in the query so the cheapest QUALIFYING deal is selected, not the
                // cheapest overall (two pack sizes resolved to one product = two active deals; a purchase
                // at the dearer deal's price must still match). Multiplication, not division — dividing
                // purchaseUnitPrice by (1 + tolerance) would introduce rounding this comparison doesn't have.
                && p.UnitPrice != null
                && p.UnitPrice * (1m + tolerance) >= purchaseUnitPrice)
            .OrderBy(p => p.UnitPrice)
            .ThenBy(p => p.Price)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlySet<Guid>> ProductIdsWithAnyObservationAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var idList = productIds.Distinct().ToList();
        if (idList.Count == 0)
            return new HashSet<Guid>();

        var found = await db.PriceObservations
            .Where(p => idList.Contains(p.ProductId) && p.SupersededById == null)
            .Select(p => p.ProductId)
            .Distinct()
            .ToListAsync(ct);
        return found.ToHashSet();
    }

    /// <summary>Batch counterpart to <see cref="LatestForProductAsync"/> (plantry-hbol): fetches every
    /// candidate purchase/manual row for the wanted products in one query, then picks the latest per
    /// product client-side — mirrors <c>CookEventRepository.GetLatestCookedAtByPlannedDishIdsAsync</c>'s
    /// materialize-then-group-by pattern.</summary>
    public async Task<IReadOnlyDictionary<Guid, PriceObservation>> LatestForProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var idList = productIds.Distinct().ToList();
        if (idList.Count == 0)
            return new Dictionary<Guid, PriceObservation>();

        var rows = await db.PriceObservations
            .Where(p => idList.Contains(p.ProductId)
                && (p.Source == PriceSource.Purchase || p.Source == PriceSource.Manual)
                && p.SupersededById == null)
            .ToListAsync(ct);

        return rows
            .GroupBy(p => p.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.ObservedAt).First());
    }

    /// <summary>Batch counterpart to <see cref="CheapestActiveDealForProductAsync"/> (plantry-hbol):
    /// fetches every candidate active-deal row for the wanted products in one query, then picks the
    /// cheapest per product client-side.</summary>
    public async Task<IReadOnlyDictionary<Guid, PriceObservation>> CheapestActiveDealsForProductsAsync(
        IEnumerable<Guid> productIds, DateOnly today, CancellationToken ct = default)
    {
        var idList = productIds.Distinct().ToList();
        if (idList.Count == 0)
            return new Dictionary<Guid, PriceObservation>();

        var rows = await db.PriceObservations
            .Where(p => idList.Contains(p.ProductId)
                && p.Source == PriceSource.Deal
                && p.ValidFrom <= today
                && p.ValidTo >= today
                && p.SupersededById == null)
            .ToListAsync(ct);

        // Nulls-last tiebreak is deliberate: Postgres `ORDER BY unit_price ASC` (used by the
        // single-product CheapestActiveDealForProductAsync) sorts NULLs last, but LINQ-to-Objects
        // OrderBy on a nullable sorts nulls first. Without the explicit HasValue key, a
        // pack-size-less deal (UnitPrice == null) would win here even when a cheaper costable
        // deal exists, diverging from the single-product path and defeating IsCostable's
        // unitless-deal exclusion.
        return rows
            .GroupBy(p => p.ProductId)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(p => p.UnitPrice.HasValue ? 0 : 1)
                .ThenBy(p => p.UnitPrice)
                .ThenBy(p => p.Price)
                .First());
    }
}
