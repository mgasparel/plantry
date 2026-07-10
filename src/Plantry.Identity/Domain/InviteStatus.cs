namespace Plantry.Identity.Domain;

/// <summary>
/// Lifecycle of a <see cref="HouseholdInvite"/>. <c>Pending</c> is the only state a token can be
/// accepted or revoked from; <c>Accepted</c>/<c>Revoked</c> are terminal. <c>Expired</c> exists in
/// the schema for a future lapse sweep — acceptance itself rejects an expired-but-still-pending
/// invite by checking <c>expires_at</c> (invariant R5), so no live path writes <c>Expired</c> yet.
/// Persisted as lowercase text with a DB CHECK constraint (Gate 7 enum convention).
/// </summary>
public enum InviteStatus { Pending, Accepted, Revoked, Expired }

public static class InviteStatusExtensions
{
    public static string ToDbValue(this InviteStatus status) => status switch
    {
        InviteStatus.Pending => "pending",
        InviteStatus.Accepted => "accepted",
        InviteStatus.Revoked => "revoked",
        InviteStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static InviteStatus Parse(string value) => value switch
    {
        "pending" => InviteStatus.Pending,
        "accepted" => InviteStatus.Accepted,
        "revoked" => InviteStatus.Revoked,
        "expired" => InviteStatus.Expired,
        _ => throw new ArgumentException($"Unknown invite status '{value}'.", nameof(value)),
    };
}
