namespace Plantry.Inventory.Domain;

/// <summary>
/// The why-taxonomy on every <see cref="StockJournalEntry"/> (ADR-011, DM-14). <c>Purchase</c>
/// and <c>Correction</c> are the addition reasons (positive delta via <see cref="ProductStock.AddStock"/>);
/// <c>Consumed</c>, <c>Discarded</c>, and <c>Correction</c> are removal reasons (negative delta
/// via <see cref="ProductStock.Consume"/>). <c>Correction</c> is bidirectional: a positive
/// correction row represents stock discovered during a Take Stock walk (Phase 4); a negative
/// correction row is a manual fix-up that removes phantom stock. Critically distinguishes
/// <c>Consumed</c> (used) from <c>Discarded</c> (wasted/expired) so waste analysis is possible
/// (VISION Phase 4).
/// </summary>
public enum StockReason
{
    Purchase,
    Consumed,
    Discarded,
    Correction,
}

public static class StockReasonExtensions
{
    public static StockReason Parse(string value) => value switch
    {
        "Purchase" => StockReason.Purchase,
        "Consumed" => StockReason.Consumed,
        "Discarded" => StockReason.Discarded,
        "Correction" => StockReason.Correction,
        _ => throw new ArgumentException($"Unknown stock reason '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this StockReason reason) => reason switch
    {
        StockReason.Purchase => "Purchase",
        StockReason.Consumed => "Consumed",
        StockReason.Discarded => "Discarded",
        StockReason.Correction => "Correction",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>
    /// True for the reasons that <see cref="ProductStock.AddStock"/> may write (positive delta).
    /// <c>Purchase</c> is the normal intake reason; <c>Correction</c> is used by Take Stock when
    /// a walk discovers more stock than recorded (Phase 4 / P4-1).
    /// </summary>
    public static bool IsAddition(this StockReason reason) =>
        reason is StockReason.Purchase or StockReason.Correction;

    /// <summary>True for the removal reasons that <see cref="ProductStock.Consume"/> may write.</summary>
    public static bool IsRemoval(this StockReason reason) => reason != StockReason.Purchase;
}
