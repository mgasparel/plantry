namespace Plantry.Inventory.Domain;

/// <summary>One lot's contribution to a <see cref="ProductStock.Consume"/> call, in that lot's own unit.</summary>
public sealed record LotDeduction(StockEntryId EntryId, decimal Amount, Guid UnitId);

/// <summary>
/// The result of <see cref="ProductStock.MarkOpened"/> — the lot's id, its expiry after the opening
/// clamp (DM-11 rule 1–2), and whether an after-opening default was actually resolved
/// (<see cref="DefaultApplied"/> false means rule 4 fired: <c>IsOpen</c> flipped but the expiry was
/// left untouched because no default is configured anywhere). The UI/toast branches on
/// <see cref="DefaultApplied"/> rather than on whether <see cref="ExpiryDate"/> happens to have
/// changed value, since a tight clamp can legitimately leave the date unchanged even when a default
/// was applied.
/// </summary>
public sealed record MarkOpenedOutcome(StockEntryId EntryId, DateOnly? ExpiryDate, bool DefaultApplied);

/// <summary>The result of <see cref="ProductStock.UnmarkOpened"/> — the lot's id and its (unrestored) expiry.</summary>
public sealed record UnmarkOpenedOutcome(StockEntryId EntryId, DateOnly? ExpiryDate);

/// <summary>
/// The result of a <see cref="ProductStock.Consume"/> call: which lots were deducted and by how
/// much, plus any <see cref="ShortfallAmount"/> (expressed in the requested unit) that the pantry
/// could not satisfy. A shortfall is reported, never an over-deduction (ADR-011).
/// <see cref="AutoOpened"/> (plantry-1le6 rule 5) lists every lot this call flipped open — a partial
/// deduction from a still-sealed lot — so the caller can fold "Marked opened — now expires …" into
/// its own feedback; empty when nothing was auto-opened.
/// </summary>
public sealed record ConsumeOutcome(
    IReadOnlyList<LotDeduction> Deductions,
    decimal ShortfallAmount,
    Guid RequestUnitId,
    IReadOnlyList<MarkOpenedOutcome> AutoOpened)
{
    public bool HasShortfall => ShortfallAmount > 0m;
}
