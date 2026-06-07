using Plantry.SharedKernel.Domain;

namespace Plantry.SharedKernel;

/// <summary>A quantity with its unit identifier.</summary>
public sealed class Quantity : ValueObject
{
    public decimal Amount { get; }

    /// <summary>ID of the Unit this quantity is expressed in.</summary>
    public Guid UnitId { get; }

    private Quantity() { } // EF

    public Quantity(decimal amount, Guid unitId)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Quantity cannot be negative.");
        Amount = amount;
        UnitId = unitId;
    }

    public Quantity WithAmount(decimal amount) => new(amount, UnitId);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return UnitId;
    }

    public override string ToString() => $"{Amount} (unit:{UnitId})";
}
