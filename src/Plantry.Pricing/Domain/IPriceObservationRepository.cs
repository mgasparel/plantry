using Plantry.SharedKernel;

namespace Plantry.Pricing.Domain;

public interface IPriceObservationRepository
{
    Task AddAsync(PriceObservation observation, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Purchase observations awaiting store resolution (DM-16 backfill): <c>source = Purchase</c>,
    /// <c>store_id IS NULL</c>, and a non-blank <c>merchant_text</c>. Household-scoped by the EF query
    /// filter + RLS; entities are tracked so <see cref="PriceObservation.ResolveStore"/> mutations persist
    /// on the next <see cref="SaveChangesAsync"/>. Blank-merchant rows are excluded — they have no name to
    /// resolve a store from. Deliberately stays <c>Purchase</c>-only — <see cref="PriceSource.Manual"/> rows
    /// have no merchant to resolve a store from and are never eligible for this sweep (plantry-3fqm).</summary>
    Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default);

    /// <summary>Latest <c>purchase</c> or <c>manual</c> observation for a product (source-filtered —
    /// a deal row never contaminates purchase costing). A household-entered <see cref="PriceSource.Manual"/>
    /// estimate is treated the same as a purchase here (plantry-3fqm) — either is superseded the moment a
    /// newer observation of either kind lands, since both compete purely on <c>observed_at</c>.</summary>
    Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Latest <c>purchase</c> or <c>manual</c> observation for a SKU (source-filtered).</summary>
    Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default);

    /// <summary>Cheapest active deal for a product: <c>source='deal'</c> whose validity window
    /// contains <paramref name="today"/>, lowest <c>unit_price</c> (DM-17 read model). Null when no
    /// deal is active.</summary>
    Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default);
}
