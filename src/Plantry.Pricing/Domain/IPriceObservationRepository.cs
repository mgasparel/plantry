using Plantry.SharedKernel;

namespace Plantry.Pricing.Domain;

public interface IPriceObservationRepository
{
    Task AddAsync(PriceObservation observation, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Single observation by id — used to look up the live row being amended (ADR-023 A7). Returns
    /// the row regardless of its <c>superseded_by_id</c> state (unlike every other read below) so that a
    /// caller mistakenly passing an already-superseded id still gets a row back, and
    /// <see cref="PriceObservation.Supersede"/> can throw its own guard rather than the lookup silently
    /// returning null.</summary>
    Task<PriceObservation?> FindAsync(PriceObservationId id, CancellationToken ct = default);

    /// <summary>Purchase observations awaiting store resolution (DM-16 backfill): <c>source = Purchase</c>,
    /// <c>store_id IS NULL</c>, and a non-blank <c>merchant_text</c>. Household-scoped by the EF query
    /// filter + RLS; entities are tracked so <see cref="PriceObservation.ResolveStore"/> mutations persist
    /// on the next <see cref="SaveChangesAsync"/>. Blank-merchant rows are excluded — they have no name to
    /// resolve a store from. Deliberately stays <c>Purchase</c>-only — <see cref="PriceSource.Manual"/> rows
    /// have no merchant to resolve a store from and are never eligible for this sweep (plantry-3fqm).
    /// Also filters <c>superseded_by_id IS NULL</c> (ADR-023 A7) — an amended-away row is dead history, not
    /// a candidate for store resolution.</summary>
    Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default);

    /// <summary>Latest <c>purchase</c> or <c>manual</c> observation for a product (source-filtered —
    /// a deal row never contaminates purchase costing). A household-entered <see cref="PriceSource.Manual"/>
    /// estimate is treated the same as a purchase here (plantry-3fqm) — either is superseded the moment a
    /// newer observation of either kind lands, since both compete purely on <c>observed_at</c>. Also filters
    /// <c>superseded_by_id IS NULL</c> (ADR-023 A7) — a row replaced by a purchase-entry amendment is never
    /// "latest", regardless of its <c>observed_at</c>.</summary>
    Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Latest <c>purchase</c> or <c>manual</c> observation for a SKU (source-filtered). Also filters
    /// <c>superseded_by_id IS NULL</c> (ADR-023 A7).</summary>
    Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default);

    /// <summary>Cheapest active deal for a product: <c>source='deal'</c> whose validity window
    /// contains <paramref name="today"/>, lowest <c>unit_price</c> (DM-17 read model). Null when no
    /// deal is active. Also filters <c>superseded_by_id IS NULL</c> (ADR-023 A7) — a superseded deal
    /// observation never competes for cheapest-active.</summary>
    Task<PriceObservation?> CheapestActiveDealForProductAsync(Guid productId, DateOnly today, CancellationToken ct = default);

    /// <summary>Batch existence check (Tidy Up D5, tidy-up.md §3): of the given product ids, which have at
    /// least one live (<c>superseded_by_id IS NULL</c>) price observation of any <see cref="PriceSource"/>.
    /// Lets D5 find products with zero price data in one round trip instead of a per-product query — the
    /// same batching convention D1/D2 established (plantry-4t0g). Superseded-filtered like every other read
    /// here (ADR-023 A7): a fully-amended-away observation doesn't count as "has price data."</summary>
    Task<IReadOnlySet<Guid>> ProductIdsWithAnyObservationAsync(IEnumerable<Guid> productIds, CancellationToken ct = default);
}
