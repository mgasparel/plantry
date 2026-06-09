using Plantry.SharedKernel;

namespace Plantry.Inventory.Domain;

/// <summary>
/// The unit-conversion seam <see cref="ProductStock.Consume"/> needs without reaching into the
/// Catalog context (PHASE-1-PLAN.md §dependency rules). Inventory.Domain references only the
/// SharedKernel; the implementation that wraps Catalog's <c>UnitConverter</c> for a specific
/// product is supplied per-call from the composition root (Plantry.Web).
///
/// <see cref="Convert"/> must <b>fail loudly</b> (return an <see cref="Error"/>) when no
/// conversion is known — never silently return an identity or zero (cross-cutting-behaviour.md).
/// </summary>
public interface IQuantityConverter
{
    Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId);
}
