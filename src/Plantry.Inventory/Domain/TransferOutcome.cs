namespace Plantry.Inventory.Domain;

/// <summary>
/// Which storage-type transition a <see cref="ProductStock.Transfer"/> call represents (plantry-6owm
/// rule 2) — derived implicitly from the source/destination locations' frozen-ness, never a separate
/// "this is a freeze" input. <see cref="Freeze"/> sets <see cref="StockEntry.FrozenAt"/>;
/// <see cref="Thaw"/> sets <see cref="StockEntry.ThawedAt"/>; <see cref="Move"/> touches neither.
/// </summary>
public enum TransferKind
{
    /// <summary>Same storage type either side (ambient→ambient or frozen→frozen) — expiry and timestamps untouched.</summary>
    Move,

    /// <summary>Non-frozen → frozen. Recomputes expiry via the after-freezing default (rule 3, replace-outright — may extend).</summary>
    Freeze,

    /// <summary>Frozen → non-frozen. Recomputes expiry via the after-thawing default (rule 3, replace-outright).</summary>
    Thaw,
}

/// <summary>
/// The result of <see cref="ProductStock.Transfer"/> — which lot(s) moved and how. A full-lot move
/// (<paramref name="Quantity"/> == the lot's original quantity) moves <paramref name="SourceEntryId"/>
/// in place and <paramref name="SplitEntryId"/> is null. A partial move (rule 1) splits: the source
/// lot keeps its location/expiry with the reduced remainder, and <paramref name="SplitEntryId"/> names
/// the new lot created at the destination. <paramref name="ExpiryDate"/> is the moved portion's expiry
/// after the transition's recompute rule (or unchanged, for a plain move or no default configured);
/// <paramref name="DefaultApplied"/> mirrors <see cref="MarkOpenedOutcome.DefaultApplied"/> — false means
/// no after-freezing/after-thawing default is configured anywhere, so the timestamp still records but
/// the expiry is left untouched (rule 6).
/// </summary>
public sealed record TransferOutcome(
    StockEntryId SourceEntryId,
    StockEntryId? SplitEntryId,
    decimal Quantity,
    Guid UnitId,
    Guid DestinationLocationId,
    TransferKind Kind,
    DateOnly? ExpiryDate,
    bool DefaultApplied);
