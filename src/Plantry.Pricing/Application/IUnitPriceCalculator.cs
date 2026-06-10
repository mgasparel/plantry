namespace Plantry.Pricing.Application;

/// <summary>
/// Port: normalizes a purchase price to a per-base-unit price. Returns null on any
/// resolution failure (soft-fail, pricing.md resolved-call #2). Implemented in Plantry.Web.
/// </summary>
public interface IUnitPriceCalculator
{
    Task<decimal?> TryNormalizeAsync(decimal price, decimal quantity, Guid unitId, CancellationToken ct = default);
}
