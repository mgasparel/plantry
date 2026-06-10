namespace Plantry.Pricing.Domain;

public readonly record struct PriceObservationId(Guid Value)
{
    public static PriceObservationId New() => new(Guid.CreateVersion7());
    public static PriceObservationId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
