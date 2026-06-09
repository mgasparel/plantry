namespace Plantry.Inventory.Domain;

/// <summary>
/// The why-taxonomy on every <see cref="StockJournalEntry"/> (ADR-011, DM-14). <c>Purchase</c> is
/// the only positive (intake) reason; the rest are removals. Critically distinguishes
/// <c>Consumed</c> (used) from <c>Discarded</c> (wasted/expired) so waste analysis is possible
/// (VISION Phase 4); <c>Correction</c> covers manual fix-ups.
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

    /// <summary>True for the removal reasons that <see cref="ProductStock.Consume"/> may write.</summary>
    public static bool IsRemoval(this StockReason reason) => reason != StockReason.Purchase;
}
