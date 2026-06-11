using Plantry.SharedKernel.Domain;

namespace Plantry.SharedKernel;

/// <summary>Currency-aware monetary value. All amounts stored in minor units to avoid floating-point drift.</summary>
public sealed class Money : ValueObject
{
    /// <summary>Amount in minor units (e.g. cents for USD/GBP).</summary>
    public long MinorUnits { get; }

    /// <summary>ISO 4217 currency code (upper-case, e.g. "GBP").</summary>
    public string Currency { get; }

    private Money() { Currency = string.Empty; } // EF

    public Money(long minorUnits, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (currency.Length != 3) throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(currency));
        MinorUnits = minorUnits;
        Currency = currency.ToUpperInvariant();
    }

    public static Money Zero(string currency) => new(0, currency);

    public bool IsZero() => MinorUnits == 0;

    public static Money FromDecimal(decimal amount, string currency, int decimalPlaces = 2)
    {
        var factor = (long)Math.Pow(10, decimalPlaces);
        return new((long)Math.Round(amount * factor, MidpointRounding.AwayFromZero), currency);
    }

    public decimal ToDecimal(int decimalPlaces = 2) =>
        (decimal)MinorUnits / (decimal)Math.Pow(10, decimalPlaces);

    public Money Add(Money other)
    {
        AssertSameCurrency(other);
        return new(MinorUnits + other.MinorUnits, Currency);
    }

    public Money Subtract(Money other)
    {
        AssertSameCurrency(other);
        return new(MinorUnits - other.MinorUnits, Currency);
    }

    private void AssertSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Cannot mix currencies: {Currency} vs {other.Currency}");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return MinorUnits;
        yield return Currency;
    }

    public override string ToString() => $"{ToDecimal()} {Currency}";
}
