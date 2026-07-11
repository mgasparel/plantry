namespace Plantry.Catalog.Domain;

/// <summary>
/// How quantities in this unit are rendered at the presentation edge (quantity-display.md Q2).
/// <see cref="Decimal"/> keeps the historical <c>0.###</c> rendering; <see cref="Fraction"/> opts the
/// unit into vulgar-fraction display ("½ cup"). This is a <b>display-only</b> flag — it changes no
/// stored quantity, scaling math, consumption, or availability comparison. Households choose per unit
/// on the Catalog → Units page; the code cannot know "cup is imperial", so the choice is explicit
/// rather than heuristic.
/// </summary>
public enum DisplayStyle
{
    /// <summary>Render quantities as decimals (<c>0.###</c>) — the default for every unit.</summary>
    Decimal = 0,

    /// <summary>Render quantities as vulgar fractions where they snap ("½ cup", "1¾ tsp").</summary>
    Fraction = 1,
}

public static class DisplayStyleExtensions
{
    public static DisplayStyle Parse(string value) => value switch
    {
        "decimal" => DisplayStyle.Decimal,
        "fraction" => DisplayStyle.Fraction,
        _ => throw new ArgumentException($"Unknown display style '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this DisplayStyle style) => style switch
    {
        DisplayStyle.Decimal => "decimal",
        DisplayStyle.Fraction => "fraction",
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };
}
