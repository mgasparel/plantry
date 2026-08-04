using Plantry.SharedKernel;

namespace Plantry.Pantry.Domain;

/// <summary>
/// The unit-conversion seam <see cref="ProductStock.Consume"/> needs. This domain layer still
/// depends only on SharedKernel (PHASE-1-PLAN.md §dependency rules), so the converter arrives as
/// an argument rather than the domain calling <c>UnitConverter</c> itself; the implementation that
/// wraps Catalog's <c>UnitConverter</c> for a specific product is supplied by
/// <see cref="Plantry.Pantry.Application.IProductConversionProvider"/>'s adapter in
/// <c>Plantry.Pantry.Application</c> — an intra-context Pantry collaboration now that Catalog and
/// Inventory live in one assembly (ADR-024, plantry-g3da.6).
///
/// <see cref="Convert"/> must <b>fail loudly</b> (return an <see cref="Error"/>) when no
/// conversion is known — never silently return an identity or zero (cross-cutting-behaviour.md).
/// </summary>
public interface IQuantityConverter
{
    Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId);
}
