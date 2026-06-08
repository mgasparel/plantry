namespace Plantry.Catalog.Domain;

public readonly record struct UnitId(Guid Value)
{
    public static UnitId New() => new(Guid.CreateVersion7());
    public static UnitId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct CategoryId(Guid Value)
{
    public static CategoryId New() => new(Guid.CreateVersion7());
    public static CategoryId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct LocationId(Guid Value)
{
    public static LocationId New() => new(Guid.CreateVersion7());
    public static LocationId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());
    public static ProductId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct ProductSkuId(Guid Value)
{
    public static ProductSkuId New() => new(Guid.CreateVersion7());
    public static ProductSkuId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct ProductConversionId(Guid Value)
{
    public static ProductConversionId New() => new(Guid.CreateVersion7());
    public static ProductConversionId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
