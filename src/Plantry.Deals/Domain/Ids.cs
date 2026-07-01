namespace Plantry.Deals.Domain;

/// <summary>Strongly-typed identity for a <see cref="StoreSubscription"/> (UUIDv7).</summary>
public readonly record struct StoreSubscriptionId(Guid Value)
{
    public static StoreSubscriptionId New() => new(Guid.CreateVersion7());
    public static StoreSubscriptionId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identity for a <see cref="FlyerImport"/> (UUIDv7).</summary>
public readonly record struct FlyerImportId(Guid Value)
{
    public static FlyerImportId New() => new(Guid.CreateVersion7());
    public static FlyerImportId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identity for a <see cref="Deal"/> (UUIDv7).</summary>
public readonly record struct DealId(Guid Value)
{
    public static DealId New() => new(Guid.CreateVersion7());
    public static DealId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly-typed identity for a <see cref="DealMatchMemory"/> (UUIDv7).</summary>
public readonly record struct DealMatchMemoryId(Guid Value)
{
    public static DealMatchMemoryId New() => new(Guid.CreateVersion7());
    public static DealMatchMemoryId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
