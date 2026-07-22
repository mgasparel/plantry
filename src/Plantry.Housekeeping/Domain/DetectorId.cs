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

    /// <summary>D3 (tidy-up.md §3): a product with an active stock lot whose expiry date is past today.</summary>
    public static readonly DetectorId StockExpired = new("stock-expired");

    /// <summary>D4 (tidy-up.md §3): a frequently-purchased product with no low-stock threshold set.</summary>
    public static readonly DetectorId StapleNoLowStockAlert = new("staple-no-low-stock-alert");

    /// <summary>D5 (tidy-up.md §3): a tracked product used in a recipe with zero price observations.</summary>
    public static readonly DetectorId RecipeIngredientNoPriceData = new("recipe-ingredient-no-price-data");

    /// <summary>D6 (tidy-up.md §3): a product whose active lots span units with no mutual conversion.</summary>
    public static readonly DetectorId StockMixedIncompatibleUnits = new("stock-mixed-incompatible-units");

    /// <summary>D7 (tidy-up.md §3, redefined): a recipe ingredient line whose product is untracked.</summary>
    public static readonly DetectorId RecipeLineUntrackedProduct = new("recipe-line-untracked-product");

    public override string ToString() => Value;
}
