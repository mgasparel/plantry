namespace Plantry.Inventory.Domain;

/// <summary>
/// Composite identity of a <see cref="ProductStock"/> aggregate (inventory.md / ADR-010): one
/// row per product per household. Catalog's <c>ProductId</c> crosses the context boundary as a
/// raw <see cref="Guid"/> soft ref (no cross-context FK), consistent with <c>Quantity.UnitId</c>.
/// </summary>
public readonly record struct ProductStockId(Guid HouseholdId, Guid ProductId)
{
    public override string ToString() => $"{HouseholdId}/{ProductId}";
}

public readonly record struct StockEntryId(Guid Value)
{
    public static StockEntryId New() => new(Guid.CreateVersion7());
    public static StockEntryId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct JournalId(Guid Value)
{
    public static JournalId New() => new(Guid.CreateVersion7());
    public static JournalId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
