namespace Plantry.Inventory.Domain;

/// <summary>One lot's contribution to a <see cref="ProductStock.Consume"/> call, in that lot's own unit.</summary>
public sealed record LotDeduction(StockEntryId EntryId, decimal Amount, Guid UnitId);

/// <summary>
/// The result of a <see cref="ProductStock.Consume"/> call: which lots were deducted and by how
/// much, plus any <see cref="ShortfallAmount"/> (expressed in the requested unit) that the pantry
/// could not satisfy. A shortfall is reported, never an over-deduction (ADR-011).
/// </summary>
public sealed record ConsumeOutcome(
    IReadOnlyList<LotDeduction> Deductions,
    decimal ShortfallAmount,
    Guid RequestUnitId)
{
    public bool HasShortfall => ShortfallAmount > 0m;
}
