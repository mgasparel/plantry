namespace Plantry.Inventory.Domain;

/// <summary>
/// The orthogonal "what triggered this entry" axis on a <see cref="StockJournalEntry"/> (DM-14),
/// separate from the why-taxonomy <see cref="StockReason"/>. Nullable on the journal — not every
/// row needs a source. <c>Cook</c> arrived in Phase 2. <c>Eat</c> (plantry-zcbx) stamps the
/// product-dish "Eat" consume/undo pair written from the meal plan's Cook strip — kept distinct from
/// <c>Cook</c> so the pantry History provenance chip (<c>StockProvenanceReaderAdapter</c>) never
/// mis-attributes an Eat row to a recipe cook; an Eat row simply renders as plain text there today
/// (no dedicated chip), same as any other unresolved source type.
/// </summary>
public enum StockSourceType
{
    Manual,
    Intake,
    Cook,
    Eat,
}

public static class StockSourceTypeExtensions
{
    public static StockSourceType Parse(string value) => value switch
    {
        "Manual" => StockSourceType.Manual,
        "Intake" => StockSourceType.Intake,
        "Cook" => StockSourceType.Cook,
        "Eat" => StockSourceType.Eat,
        _ => throw new ArgumentException($"Unknown stock source type '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this StockSourceType source) => source switch
    {
        StockSourceType.Manual => "Manual",
        StockSourceType.Intake => "Intake",
        StockSourceType.Cook => "Cook",
        StockSourceType.Eat => "Eat",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}
