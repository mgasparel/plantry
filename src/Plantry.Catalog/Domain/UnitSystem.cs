namespace Plantry.Catalog.Domain;

/// <summary>
/// The measurement system a unit belongs to (quantity-display.md Q5, amended 2026-07-11). This is the
/// explicit <b>metric/imperial firewall</b> for unit simplification: <see cref="QuantityDisplay.Simplify"/>
/// only re-expresses a scaled quantity within units sharing the same non-<see cref="Unspecified"/> system,
/// so a metric amount can never be rewritten as an imperial one (or vice versa) — regardless of whether a
/// whole-number conversion ratio happens to exist. The tag makes the family boundary a stated fact rather
/// than an arithmetic coincidence (see quantity-display-seed-factor-gap.md). Like <see cref="DisplayStyle"/>
/// this is a <b>display-only</b> classification — it changes no stored quantity, scaling math, consumption,
/// or availability comparison. Households classify units on the Catalog → Units page; the code cannot know
/// "cup is US customary", so the choice is explicit rather than heuristic.
/// </summary>
public enum UnitSystem
{
    /// <summary>System not stated — the default. A unit tagged <see cref="Unspecified"/> anchors no
    /// simplification family: it is never proposed as a target and, when authored, never simplifies. Count
    /// units stay here (count-dimension simplification is deliberately out of scope).</summary>
    Unspecified = 0,

    /// <summary>Metric system (ml, l, g, kg, mg).</summary>
    Metric = 1,

    /// <summary>US customary system (oz, lb, fl oz, cup, tsp, tbsp).</summary>
    UsCustomary = 2,
}

public static class UnitSystemExtensions
{
    public static UnitSystem Parse(string value) => value switch
    {
        "unspecified" => UnitSystem.Unspecified,
        "metric" => UnitSystem.Metric,
        "us_customary" => UnitSystem.UsCustomary,
        _ => throw new ArgumentException($"Unknown unit system '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this UnitSystem system) => system switch
    {
        UnitSystem.Unspecified => "unspecified",
        UnitSystem.Metric => "metric",
        UnitSystem.UsCustomary => "us_customary",
        _ => throw new ArgumentOutOfRangeException(nameof(system)),
    };
}
