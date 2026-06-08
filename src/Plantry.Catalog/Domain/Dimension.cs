namespace Plantry.Catalog.Domain;

/// <summary>The measurement dimension a unit belongs to (catalog.md `unit.dimension`).</summary>
public enum Dimension
{
    Mass,
    Volume,
    Count,
}

public static class DimensionExtensions
{
    public static Dimension Parse(string value) => value switch
    {
        "mass" => Dimension.Mass,
        "volume" => Dimension.Volume,
        "count" => Dimension.Count,
        _ => throw new ArgumentException($"Unknown dimension '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this Dimension dimension) => dimension switch
    {
        Dimension.Mass => "mass",
        Dimension.Volume => "volume",
        Dimension.Count => "count",
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };
}
