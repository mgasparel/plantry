namespace Plantry.Planning.Application;

/// <summary>
/// Anti-corruption port: Shopping's basket cost estimate (plantry-e016, stats-injection appendix) needs
/// each product's <b>effective</b> price observation — the cheapest active deal if one covers it, else the
/// latest Purchase/Manual observation — from the Pricing context. Defined here in Shopping.Application and
/// implemented in the Web layer over <c>Market.Application.PricingQueries.EffectiveCostablePricesAsync</c>, the same
/// ACL shape as <see cref="IShoppingDealReader"/> (P5-9) but returning the raw price/quantity/unit rather
/// than deal metadata — the two ports serve different needs (a badge vs. a computed line cost) over the same
/// underlying read model.
/// </summary>
public interface IShoppingPriceReader
{
    /// <summary>
    /// Resolves the effective price observation for each of <paramref name="productIds"/> that has one,
    /// evaluated against <paramref name="today"/> so a deal's window applies deterministically (mirrors
    /// <see cref="IShoppingDealReader.GetActiveDealsAsync"/>). Products with neither an active deal nor any
    /// prior purchase/manual observation are simply absent from the result — never a guessed price.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ShoppingPriceEstimate>> GetEffectivePricesAsync(
        IReadOnlyList<Guid> productIds,
        DateOnly today,
        CancellationToken ct = default);
}

/// <summary>
/// One product's effective price observation, in the observation's own recorded unit/quantity (e.g. "$4.49
/// for 2 L" — <see cref="Price"/> = 4.49, <see cref="Quantity"/> = 2, <see cref="UnitId"/> = the litre unit).
/// The per-unit price is derived as <see cref="Price"/> / <see cref="Quantity"/> by the caller — deliberately
/// NOT a pre-normalized unit price, mirroring the plantry-1oca basis fix in <c>CostingService</c> /
/// <c>PlanCostingService</c> (the normalized <c>UnitPrice</c> field is per BASE unit of the dimension, a
/// different basis whenever the observation's unit has <c>FactorToBase != 1</c>).
/// </summary>
public sealed record ShoppingPriceEstimate(
    Guid ProductId,
    decimal Price,
    decimal Quantity,
    Guid UnitId);
