namespace Plantry.Housekeeping.Domain;

/// <summary>
/// Stable string identity for a problem detector (tidy-up.md §3 catalogue). Persisted verbatim as
/// half of a <see cref="Dismissal"/> tombstone's key (T5: <c>FindingKey = (DetectorId, SubjectId)</c>),
/// so these values are append-only — renaming one silently orphans every existing dismissal for that
/// detector (it would no longer match on the next page render, reopening all of them).
/// </summary>
public readonly record struct DetectorId(string Value)
{
    /// <summary>D1 (tidy-up.md §3): a stock lot whose unit cannot convert to the product's display unit.</summary>
    public static readonly DetectorId StockUnitUnconvertible = new("stock-unit-unconvertible");

    /// <summary>D2 (tidy-up.md §3): a tracked recipe line whose unit has no path to the product default.</summary>
    public static readonly DetectorId RecipeConversionGap = new("recipe-conversion-gap");

    public override string ToString() => Value;
}
