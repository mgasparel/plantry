using System.Security.Cryptography;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Identity.Domain;

/// <summary>
/// The "household issues invite → user accepts" join credential (ADR-010). A child of the
/// <see cref="Household"/> aggregate boundary, but persisted as its own root keyed by
/// <see cref="HouseholdId"/> so it can be resolved by <see cref="Token"/> with <b>no tenant
/// context</b> — the invitee is unauthenticated and does not yet know which household they belong
/// to. Enforces the identity-domain invariants R4 (token globally unique; only <c>pending</c>
/// tokens are valid; accept is one-way) and R5 (<see cref="ExpiresAt"/> is checked on accept).
/// </summary>
public sealed class HouseholdInvite : AggregateRoot<HouseholdInviteId>
{
    /// <summary>Default lifetime of a freshly issued invite (7 days) when the caller gives no override.</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(7);

    /// <summary>The household this invite grants membership to (the tenancy key + parent reference).</summary>
    public HouseholdId HouseholdId { get; private set; }

    /// <summary>The invitee's email (lower-cased on issue).</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>The accept-link secret. Globally unique (DB unique index); high-entropy and URL-safe.</summary>
    public string Token { get; private set; } = string.Empty;

    public InviteStatus Status { get; private set; }

    /// <summary>The Identity user who issued the invite (soft cross-reference for attribution).</summary>
    public Guid InvitedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }

    private HouseholdInvite() { } // EF

    private HouseholdInvite(
        HouseholdInviteId id, HouseholdId householdId, string email, string token,
        Guid invitedByUserId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Id = id;
        HouseholdId = householdId;
        Email = email;
        Token = token;
        Status = InviteStatus.Pending;
        InvitedByUserId = invitedByUserId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>
    /// Issues a new pending invite: generates a cryptographically random token and sets the expiry
    /// window (<paramref name="validity"/> or <see cref="DefaultValidity"/>) from the injected clock.
    /// </summary>
    public static HouseholdInvite Issue(
        HouseholdId householdId, string email, Guid invitedByUserId, IClock clock, TimeSpan? validity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var now = clock.UtcNow;
        return new HouseholdInvite(
            HouseholdInviteId.New(),
            householdId,
            email.Trim().ToLowerInvariant(),
            GenerateToken(),
            invitedByUserId,
            now,
            now + (validity ?? DefaultValidity));
    }

    /// <summary>
    /// Checks — without mutating — whether the invite is acceptable right now: it must be pending (R4)
    /// and unexpired (R5, evaluated against <paramref name="clock"/>). This is the read-only half of
    /// <see cref="Accept"/>, factored out so the join flow can validate a token on GET (to decide
    /// between showing the registration form and a friendly dead-end) using the exact same rule that
    /// governs the eventual accept, with no side effect. Returns the specific failure (<c>Invite.NotPending</c>
    /// / <c>Invite.Expired</c>) so a caller can tailor its message.
    /// </summary>
    public Result Validate(IClock clock)
    {
        if (Status != InviteStatus.Pending)
            return Error.Custom("Invite.NotPending", "Only a pending invite can be accepted.");

        if (clock.UtcNow >= ExpiresAt)
            return Error.Custom("Invite.Expired", "This invite has expired.");

        return Result.Success();
    }

    /// <summary>
    /// Accepts the invite: the one-way <c>pending → accepted</c> transition (R4). Fails if the invite
    /// is not pending or has expired (R5, checked against <paramref name="clock"/> via
    /// <see cref="Validate"/>). Records <see cref="AcceptedAt"/> on success.
    /// </summary>
    public Result Accept(IClock clock)
    {
        var check = Validate(clock);
        if (check.IsFailure)
            return check;

        Status = InviteStatus.Accepted;
        AcceptedAt = clock.UtcNow;
        return Result.Success();
    }

    /// <summary>Revokes a pending invite (<c>pending → revoked</c>). Fails if it is not pending.</summary>
    public Result Revoke()
    {
        if (Status != InviteStatus.Pending)
            return Error.Custom("Invite.NotPending", "Only a pending invite can be revoked.");

        Status = InviteStatus.Revoked;
        return Result.Success();
    }

    /// <summary>True when the invite's expiry window has elapsed relative to <paramref name="clock"/>.</summary>
    public bool IsExpired(IClock clock) => clock.UtcNow >= ExpiresAt;

    // 256 bits of CSPRNG entropy, hex-encoded to a 64-char URL-safe string. Hex avoids any
    // base64 '+'/'/' escaping concerns in the accept link and keeps the token opaque.
    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
