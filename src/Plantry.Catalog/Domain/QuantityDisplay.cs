using System.Globalization;

namespace Plantry.Catalog.Domain;

/// <summary>
/// Pure presentation-layer formatter for recipe quantities (quantity-display.md §3–§5). Mirrors
/// <see cref="UnitConverter"/>: side-effect free, callers supply the household's units, nothing is
/// loaded. Two behaviours:
/// <list type="bullet">
///   <item>(Q1) <see cref="FormatAmount"/> renders a quantity in a <see cref="DisplayStyle.Fraction"/>
///     unit as a vulgar-fraction glyph ("½ cup", "1¾ tsp"); <see cref="DisplayStyle.Decimal"/> keeps
///     the historical <c>0.###</c> rendering.</item>
///   <item>(Q2) <see cref="Simplify"/>, called only when the recipe scale ≠ 1, may re-express a scaled
///     amount in a same-dimension sibling unit that reads with fewer measures ("4 tbsp" → "¼ cup").</item>
/// </list>
/// Display-only: nothing here mutates stored quantities, scaling math, consumption, or availability —
/// simplification bugs can only ever produce an odd string, never a wrong deduction (Q1). The §5
/// golden table is the normative contract; the penalty weights below are an implementation detail
/// tunable within it (Q6).
/// </summary>
public static class QuantityDisplay
{
    /// <summary>A fractional remainder must be within this of a vocabulary fraction to snap (Q3).</summary>
    private const decimal SnapTolerance = 0.01m;

    /// <summary>
    /// Two <see cref="Unit.FactorToBase"/> values count as an integer multiple when the ratio is within
    /// this of a whole number (Q5). Tight enough to keep the metric/imperial firewall exact — real
    /// non-integer ratios (tbsp→ml ≈ 14.79) are nowhere near — while absorbing decimal-division noise.
    /// </summary>
    private const decimal IntegerRatioTolerance = 0.000000001m;

    /// <summary>
    /// The vulgar-fraction vocabulary (Q3): halves, thirds, quarters, eighths. Each carries its
    /// measure-count penalty for the simplification score (Q6): ½ 1 · ¼ 2 · ¾ 3 · ⅓ 3 · ⅛ 3 · ⅔ 4 ·
    /// ⅜ 4 · ⅝ 5 · ⅞ 5. A snapped whole number contributes penalty 0. Order is not significant —
    /// nearest-by-value wins the snap.
    /// </summary>
    private static readonly VulgarFraction[] Vocabulary =
    [
        new(1, 2, "½", 1),
        new(1, 4, "¼", 2),
        new(3, 4, "¾", 3),
        new(1, 3, "⅓", 3),
        new(1, 8, "⅛", 3),
        new(2, 3, "⅔", 4),
        new(3, 8, "⅜", 4),
        new(5, 8, "⅝", 5),
        new(7, 8, "⅞", 5),
    ];

    /// <summary>
    /// Renders <paramref name="amount"/> for display (Q1/§3). In <see cref="DisplayStyle.Decimal"/> —
    /// or for any non-positive amount ("to taste" / zero lines are untouched by this feature) — the
    /// historical <c>0.###</c> rendering. In <see cref="DisplayStyle.Fraction"/>: the whole part plus a
    /// glyph with no separator ("1½"), a glyph alone when the whole part is 0 ("½"), or the whole part
    /// alone when the remainder carries ("1"); if the remainder snaps to no vocabulary fraction within
    /// <see cref="SnapTolerance"/>, the whole amount falls back to <c>0.###</c>. Unit codes are appended
    /// by the caller — this returns the number only.
    /// </summary>
    public static string FormatAmount(decimal amount, DisplayStyle style)
    {
        if (style == DisplayStyle.Decimal || amount <= 0)
            return amount.ToString("0.###", CultureInfo.InvariantCulture);

        if (TrySnap(amount, out var whole, out var fraction))
        {
            if (fraction is null)
                return whole.ToString(CultureInfo.InvariantCulture);

            return whole == 0
                ? fraction.Glyph
                : whole.ToString(CultureInfo.InvariantCulture) + fraction.Glyph;
        }

        return amount.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Re-expresses <paramref name="scaledAmount"/> (already in the unit <paramref name="unitId"/>) in
    /// the same-dimension sibling unit that reads with the fewest measures (Q2/§4). Call only when the
    /// recipe scale ≠ 1; at 1× the authored unit is always kept. Candidates (Q5) are the authored unit
    /// itself plus every household unit sharing its <see cref="Unit.Dimension"/>, sharing its non-
    /// <see cref="UnitSystem.Unspecified"/> <see cref="Unit.UnitSystem"/> (the explicit metric/imperial
    /// firewall — tbsp never proposes ml even though 15:1 is whole), styled
    /// <see cref="DisplayStyle.Fraction"/>, and in a whole-number conversion ratio to it (the math
    /// guarantee that the rewrite lands on clean fractions — tbsp→cup = 16 qualifies). An authored unit
    /// tagged <see cref="UnitSystem.Unspecified"/> anchors no family and always keeps its unit.
    /// Representations that do not snap under the Q3 rule are discarded (whole numbers count as
    /// snapped); the survivor with the lowest measure-count score wins, ties resolving to the authored
    /// unit and then to the larger unit (Q6). If nothing snaps in any unit the input is returned
    /// unchanged (Q7) — <see cref="FormatAmount"/> then renders the decimal fallback.
    /// </summary>
    public static (decimal Amount, Guid UnitId) Simplify(
        decimal scaledAmount, Guid unitId, IReadOnlyList<Unit> units)
    {
        var authored = units.FirstOrDefault(u => u.Id.Value == unitId);
        if (authored is null)
            return (scaledAmount, unitId);

        var candidates = new List<Unit> { authored };
        foreach (var u in units)
        {
            if (u.Id.Value == unitId) continue;
            if (u.Dimension != authored.Dimension) continue;
            // Metric/imperial firewall (Q5): only cross between units sharing the same, stated system.
            // An Unspecified authored unit anchors no family (both checks below reject every sibling),
            // and an Unspecified sibling is never a target — so metric never rewrites as imperial even
            // when a whole-number ratio coincidentally exists (cup=240 : ml=1 → 480 ml stays 480 ml).
            if (u.UnitSystem == UnitSystem.Unspecified) continue;
            if (u.UnitSystem != authored.UnitSystem) continue;
            if (u.DisplayStyle != DisplayStyle.Fraction) continue;
            // Math guarantee (Q5): a whole-number conversion ratio keeps the rewrite on clean fractions.
            if (!IsIntegerRatio(authored.FactorToBase, u.FactorToBase)) continue;
            candidates.Add(u);
        }

        Representation? best = null;
        foreach (var candidate in candidates)
        {
            var converted = scaledAmount * (authored.FactorToBase / candidate.FactorToBase);
            if (!TrySnap(converted, out var whole, out var fraction))
                continue;

            var score = whole + (fraction?.Penalty ?? 0) + (candidate.Id.Value == unitId ? 0 : 1);
            var rep = new Representation(converted, candidate, score);

            if (best is null || IsBetter(rep, best.Value, unitId))
                best = rep;
        }

        return best is { } b ? (b.Amount, b.Unit.Id.Value) : (scaledAmount, unitId);
    }

    /// <summary>Lower score wins; ties keep the authored unit, then prefer the larger unit (Q6).</summary>
    private static bool IsBetter(Representation candidate, Representation incumbent, Guid authoredUnitId)
    {
        if (candidate.Score != incumbent.Score)
            return candidate.Score < incumbent.Score;

        var candidateIsAuthored = candidate.Unit.Id.Value == authoredUnitId;
        var incumbentIsAuthored = incumbent.Unit.Id.Value == authoredUnitId;
        if (candidateIsAuthored != incumbentIsAuthored)
            return candidateIsAuthored;

        return candidate.Unit.FactorToBase > incumbent.Unit.FactorToBase;
    }

    /// <summary>
    /// Splits <paramref name="amount"/> into a whole part and a vocabulary fraction if the remainder snaps
    /// within <see cref="SnapTolerance"/>. A remainder near 0 or 1 carries into <paramref name="whole"/>
    /// with a null fraction (a plain whole number, which counts as snapped). Returns false when the
    /// remainder matches no vocabulary fraction.
    /// </summary>
    private static bool TrySnap(decimal amount, out int whole, out VulgarFraction? fraction)
    {
        var floor = Math.Floor(amount);
        whole = (int)floor;
        var remainder = amount - floor;

        if (remainder <= SnapTolerance) { fraction = null; return true; }
        if (remainder >= 1m - SnapTolerance) { whole += 1; fraction = null; return true; }

        VulgarFraction? nearest = null;
        var nearestDiff = decimal.MaxValue;
        foreach (var vf in Vocabulary)
        {
            var diff = Math.Abs(remainder - vf.Value);
            if (diff < nearestDiff) { nearestDiff = diff; nearest = vf; }
        }

        if (nearest is not null && nearestDiff <= SnapTolerance) { fraction = nearest; return true; }

        fraction = null;
        return false;
    }

    /// <summary>True when <paramref name="a"/>/<paramref name="b"/> or its inverse is a whole number (Q5).</summary>
    private static bool IsIntegerRatio(decimal a, decimal b)
    {
        if (a == 0m || b == 0m) return false;
        return IsWhole(a / b) || IsWhole(b / a);
    }

    private static bool IsWhole(decimal x) => Math.Abs(x - Math.Round(x)) <= IntegerRatioTolerance;

    /// <summary>A vocabulary fraction: its exact value, glyph, and measure-count penalty (Q6).</summary>
    private sealed record VulgarFraction(int Numerator, int Denominator, string Glyph, int Penalty)
    {
        public decimal Value { get; } = (decimal)Numerator / Denominator;
    }

    /// <summary>A snapped candidate representation and its measure-count score.</summary>
    private readonly record struct Representation(decimal Amount, Unit Unit, int Score);
}
