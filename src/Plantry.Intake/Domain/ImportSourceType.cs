namespace Plantry.Intake.Domain;

public enum ImportSourceType { Receipt }

public static class ImportSourceTypeExtensions
{
    public static string ToDbValue(this ImportSourceType t) => t switch
    {
        ImportSourceType.Receipt => "Receipt",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static ImportSourceType Parse(string value) => value switch
    {
        "Receipt" => ImportSourceType.Receipt,
        _ => throw new ArgumentException($"Unknown import source type '{value}'.", nameof(value)),
    };
}
