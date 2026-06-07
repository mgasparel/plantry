namespace Plantry.SharedKernel;

public readonly record struct HouseholdId(Guid Value)
{
    // App-generated, time-ordered UUIDv7 primary keys (persistence convention).
    public static HouseholdId New() => new(Guid.CreateVersion7());
    public static HouseholdId From(Guid value) => new(value);
    public static HouseholdId From(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString();
}
