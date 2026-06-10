namespace Plantry.Pricing.Domain;

public enum PriceSource { Purchase, Deal }

public static class PriceSourceExtensions
{
    public static string ToDbValue(this PriceSource source) => source switch
    {
        PriceSource.Purchase => "Purchase",
        PriceSource.Deal => "Deal",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static PriceSource Parse(string value) => value switch
    {
        "Purchase" => PriceSource.Purchase,
        "Deal" => PriceSource.Deal,
        _ => throw new ArgumentException($"Unknown price source '{value}'.", nameof(value)),
    };
}
