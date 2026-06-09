namespace Plantry.Inventory.Domain;

/// <summary>
/// The orthogonal "what triggered this entry" axis on a <see cref="StockJournalEntry"/> (DM-14),
/// separate from the why-taxonomy <see cref="StockReason"/>. Nullable on the journal — not every
/// row needs a source. <c>Cook</c> arrives in Phase 2.
/// </summary>
public enum StockSourceType
{
    Manual,
    Intake,
    Cook,
}

public static class StockSourceTypeExtensions
{
    public static StockSourceType Parse(string value) => value switch
    {
        "Manual" => StockSourceType.Manual,
        "Intake" => StockSourceType.Intake,
        "Cook" => StockSourceType.Cook,
        _ => throw new ArgumentException($"Unknown stock source type '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this StockSourceType source) => source switch
    {
        StockSourceType.Manual => "Manual",
        StockSourceType.Intake => "Intake",
        StockSourceType.Cook => "Cook",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}
