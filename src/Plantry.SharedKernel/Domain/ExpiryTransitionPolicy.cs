namespace Plantry.SharedKernel.Domain;

/// <summary>
/// The exhaustive normal policy for materializing expiry when stock crosses a freeze/thaw
/// boundary. Missing-catalog fallback is represented by the transfer outcome, not by a resolver
/// case, so callers cannot accidentally treat an explicit Never rule as "no default".
/// </summary>
public abstract record ExpiryTransitionPolicy
{
    private ExpiryTransitionPolicy() { }

    /// <summary>Materialize the moved lot without an expiry date.</summary>
    public sealed record Never : ExpiryTransitionPolicy;

    /// <summary>Materialize the moved lot at today plus <see cref="Value"/> days.</summary>
    public sealed record Days : ExpiryTransitionPolicy
    {
        public int Value { get; }

        public Days(int value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Value = value;
        }
    }
}
