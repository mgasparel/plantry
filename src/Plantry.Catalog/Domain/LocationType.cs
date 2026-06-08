namespace Plantry.Catalog.Domain;

/// <summary>Storage-location type that drives freeze/thaw expiry logic (catalog.md `location.type`).</summary>
public enum LocationType
{
    Ambient,
    Frozen,
}

public static class LocationTypeExtensions
{
    public static LocationType Parse(string value) => value switch
    {
        "ambient" => LocationType.Ambient,
        "frozen" => LocationType.Frozen,
        _ => throw new ArgumentException($"Unknown location type '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this LocationType type) => type switch
    {
        LocationType.Ambient => "ambient",
        LocationType.Frozen => "frozen",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
