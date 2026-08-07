namespace Plantry.Pantry.Domain;

/// <summary>
/// The why-taxonomy on every <see cref="StockJournalEntry"/> (ADR-011, DM-14). <c>Purchase</c>
/// and <c>Correction</c> are the addition reasons (positive delta via <see cref="ProductStock.AddStock"/>);
/// <c>Consumed</c>, <c>Discarded</c>, and <c>Correction</c> are removal reasons (negative delta
/// via <see cref="ProductStock.Consume"/>). <c>Correction</c> is bidirectional: a positive
/// correction row represents stock discovered during a Take Stock walk (Phase 4); a negative
/// correction row is a manual fix-up that removes phantom stock. Critically distinguishes
/// <c>Consumed</c> (used) from <c>Discarded</c> (wasted/expired) so waste analysis is possible
/// (VISION Phase 4). <c>Amendment</c> (ADR-023, purchase-entry-amendment.md) is the newest
/// addition — it represents a compensating fix to a data-entry mistake on a Purchase row, as
/// opposed to <c>Correction</c>'s "physical reality diverged from the record" meaning. It is
/// bidirectional like <c>Correction</c> but is written <b>only</b> by
/// <see cref="ProductStock.AmendPurchase"/>, never by <see cref="ProductStock.AddStock"/> or
/// <see cref="ProductStock.Consume"/> — deliberately excluded from <see cref="StockReasonExtensions.IsAddition"/>
/// and <see cref="StockReasonExtensions.IsRemoval"/> so those two general-purpose gates can never
/// admit it. <c>Cook</c> (plantry-a45c) is the addition reason for cook-produced leftovers — before
/// it existed, the composition-root yield-on-cook adapter stamped these additions as <c>Purchase</c>,
/// which misrepresented the source in stock history; provenance is carried separately via
/// <see cref="StockSourceType.Cook"/> + <c>sourceRef</c>, but the reason itself now says "Cook" too.
/// </summary>
public enum StockReason
{
    Purchase,
    Consumed,
    Discarded,
    Correction,
    Amendment,
    Cook,
}

public static class StockReasonExtensions
{
    public static StockReason Parse(string value) => value switch
    {
        "Purchase" => StockReason.Purchase,
        "Consumed" => StockReason.Consumed,
        "Discarded" => StockReason.Discarded,
        "Correction" => StockReason.Correction,
        "Amendment" => StockReason.Amendment,
        "Cook" => StockReason.Cook,
        _ => throw new ArgumentException($"Unknown stock reason '{value}'.", nameof(value)),
    };

    public static string ToDbValue(this StockReason reason) => reason switch
    {
        StockReason.Purchase => "Purchase",
        StockReason.Consumed => "Consumed",
        StockReason.Discarded => "Discarded",
        StockReason.Correction => "Correction",
        StockReason.Amendment => "Amendment",
        StockReason.Cook => "Cook",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>
    /// True for the reasons that <see cref="ProductStock.AddStock"/> may write (positive delta).
    /// <c>Purchase</c> is the normal intake reason; <c>Correction</c> is used by Take Stock when
    /// a walk discovers more stock than recorded (Phase 4 / P4-1); <c>Cook</c> (plantry-a45c) is
    /// used by the yield-on-cook adapter for recipe-produced leftovers. <c>Amendment</c> is
    /// deliberately excluded — it is bidirectional and written only by
    /// <see cref="ProductStock.AmendPurchase"/>.
    /// </summary>
    public static bool IsAddition(this StockReason reason) =>
        reason is StockReason.Purchase or StockReason.Correction or StockReason.Cook;

    /// <summary>
    /// True for the removal reasons that <see cref="ProductStock.Consume"/> may write. <c>Amendment</c>
    /// is deliberately excluded (ADR-023) even though it is not <c>Purchase</c> — it is bidirectional
    /// and written only by <see cref="ProductStock.AmendPurchase"/>, never through the general-purpose
    /// consume primitive.
    /// </summary>
    public static bool IsRemoval(this StockReason reason) =>
        reason is StockReason.Consumed or StockReason.Discarded or StockReason.Correction;
}
