namespace Plantry.Housekeeping.Domain;

/// <summary>Strongly-typed identity for a <see cref="Dismissal"/> (UUIDv7).</summary>
public readonly record struct DismissalId(Guid Value)
{
    public static DismissalId New() => new(Guid.CreateVersion7());
    public static DismissalId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
