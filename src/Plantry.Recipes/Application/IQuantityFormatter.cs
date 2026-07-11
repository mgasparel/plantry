namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption port for presentation-layer quantity formatting (quantity-display.md §3–§7).
/// Bridges Recipes' render surfaces (Details, Cook) to Catalog's pure <c>QuantityDisplay</c> formatter,
/// supplying the household's units so the render pages never load Catalog reference data directly
/// (Gate 2). Mirrors <see cref="IUnitConverter"/>: defined here in Recipes.Application and
/// <b>implemented in Plantry.Web</b> over Catalog's <c>QuantityDisplay</c> + <c>IUnitRepository</c>, so the
/// Recipes projects keep their <c>→ SharedKernel only</c> dependency. Identifiers cross as raw
/// <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public interface IQuantityFormatter
{
    /// <summary>
    /// Formats each requested quantity for display, keyed by the caller's opaque
    /// <see cref="QuantityFormatRequest.Key"/>. When <see cref="QuantityFormatRequest.Simplify"/> is
    /// <c>true</c> (Cook, scale ≠ 1) the amount may be re-expressed in a same-dimension sibling unit that
    /// reads with fewer measures (Q2 — "4 tbsp" → "¼ cup"); otherwise (Details 1×, or a Cook line at
    /// scale 1) it renders in the authored unit (Q1). The returned <see cref="FormattedQuantity.UnitId"/>
    /// is the authored unit unless simplification re-expressed it — callers resolve its display code.
    /// Requests whose unit is unknown to the household fall back to the historical <c>0.###</c> decimal
    /// in the authored unit. Keys absent from the input are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, FormattedQuantity>> FormatAsync(
        IReadOnlyList<QuantityFormatRequest> requests, CancellationToken ct = default);
}

/// <summary>
/// One quantity to format. <see cref="Key"/> is the caller's stable identity for the line (a recipe
/// ingredient id, a Cook line key, …) so results map back without positional coupling.
/// <see cref="Amount"/> is already scale-applied by the caller; the formatter only presents it.
/// <see cref="Simplify"/> gates Q2 unit simplification — pass <c>true</c> only when the recipe scale ≠ 1.
/// </summary>
public sealed record QuantityFormatRequest(string Key, decimal Amount, Guid UnitId, bool Simplify);

/// <summary>
/// A formatted quantity: the display <see cref="Amount"/> string (a vulgar-fraction glyph like "¼" /
/// "1½" for a Fraction-styled unit, else the <c>0.###</c> decimal) and the <see cref="UnitId"/> it
/// renders in — the authored unit unless Q2 simplification re-expressed it in a sibling unit.
/// </summary>
public sealed record FormattedQuantity(string Amount, Guid UnitId);
