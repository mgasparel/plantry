namespace Plantry.Shopping.Application;

/// <summary>
/// Anti-corruption read port onto <b>Pricing</b>'s cheapest-active-deal read model (ADR-010; P5-9).
/// Shopping renders the "On sale at {store} this week" badge on list items at read time by calling this
/// port — it depends on <b>Pricing, never Deals</b> (ADR-010 boundary: Pricing is the single owner of the
/// "active deal per product" read model; there is no exposed Deals active-deal port). The badge is a
/// read-time join, <b>never stored</b> on the item (shopping-domain-model.md R3 / D11).
///
/// <para>Defined here in Shopping.Application (which keeps its <c>→ SharedKernel only</c> dependency) and
/// <b>implemented in Plantry.Web</b> over <see cref="Plantry.Pricing.Application.PricingQueries"/> for the
/// cheapest-active-deal read plus Catalog's <c>IStoreRepository</c> for the merchant name — the same
/// Port + Web-adapter shape as the recipe cost badge (<c>IPriceReader</c>). All identifiers cross as raw
/// <see cref="System.Guid"/> soft-refs (DM-3).</para>
/// </summary>
public interface IShoppingDealReader
{
    /// <summary>
    /// Resolves the cheapest active deal (if any) for each of the given products, evaluated against
    /// <paramref name="today"/> so a deal appears/disappears as the validity window opens/lapses — the
    /// window filtering itself lives in Pricing's read model (P5-P/DM-17). Products with no active deal
    /// are omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ShoppingActiveDeal>> GetActiveDealsAsync(
        IReadOnlyList<Guid> productIds,
        DateOnly today,
        CancellationToken ct = default);
}

/// <summary>
/// The slice of Pricing's cheapest-active-deal observation that the Shopping badge needs: which deal is
/// active and at which store. Projected for the "On sale at {StoreName} this week" badge; never persisted.
/// </summary>
/// <param name="ProductId">The product this active deal covers.</param>
/// <param name="DealId">The deal behind the badge (the observation's source ref) — provenance for the tappable link.</param>
/// <param name="StoreId">Merchant soft-ref (→ <c>catalog.store</c>). Null when the observation carries no store.</param>
/// <param name="StoreName">
/// Resolved merchant display name via Catalog. Null when <see cref="StoreId"/> is null or unresolved —
/// the badge then falls back to "On sale this week" without a store.
/// </param>
public sealed record ShoppingActiveDeal(
    Guid ProductId,
    Guid DealId,
    Guid? StoreId,
    string? StoreName);
