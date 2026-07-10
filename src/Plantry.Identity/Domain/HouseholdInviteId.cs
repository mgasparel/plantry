namespace Plantry.Identity.Domain;

public readonly record struct HouseholdInviteId(Guid Value)
{
    // App-generated, time-ordered UUIDv7 primary keys (persistence convention).
    public static HouseholdInviteId New() => new(Guid.CreateVersion7());
    public static HouseholdInviteId From(Guid value) => new(value);
    public static HouseholdInviteId From(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString();
}
