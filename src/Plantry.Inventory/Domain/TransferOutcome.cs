namespace Plantry.Inventory.Domain;

/// <summary>Explicit expiry effect of a transfer.</summary>
public abstract record TransferExpiryEffect
{
    private TransferExpiryEffect() { }

    /// <summary>The transfer was a same-storage move, or the catalog was unavailable; expiry is untouched.</summary>
    public sealed record Unchanged : TransferExpiryEffect;

    /// <summary>The moved portion receives a date materialized from a day policy.</summary>
    public sealed record Days(DateOnly ExpiryDate) : TransferExpiryEffect;

    /// <summary>The moved portion receives no expiry date; null is the persisted no-expiry value.</summary>
    public sealed record Never : TransferExpiryEffect;
}

/// <summary>
/// Which storage-type transition a <see cref="ProductStock.Transfer"/> call represents (plantry-6owm
/// rule 2) — derived implicitly from the source/destination locations' frozen-ness, never a separate
/// "this is a freeze" input.
/// </summary>
public enum TransferKind
{
    Move,
    Freeze,
    Thaw,
}

/// <summary>
/// The result of a transfer. A full-lot move moves <paramref name="SourceEntryId"/> in place;
/// a partial move names the new destination lot in <paramref name="SplitEntryId"/>. The
/// <paramref name="ExpiryEffect"/> is deliberately separate from <paramref name="ExpiryDate"/>:
/// an unchanged lot with no expiry and a Never rule both have a null date, but are not the same
/// outcome.
/// </summary>
public sealed record TransferOutcome(
    StockEntryId SourceEntryId,
    StockEntryId? SplitEntryId,
    decimal Quantity,
    Guid UnitId,
    Guid DestinationLocationId,
    TransferKind Kind,
    TransferExpiryEffect ExpiryEffect,
    DateOnly? ExpiryDate)
{
    /// <summary>Compatibility projection for existing callers; inspect <see cref="ExpiryEffect"/> for the reason.</summary>
    public bool DefaultApplied => ExpiryEffect is not TransferExpiryEffect.Unchanged;
}
