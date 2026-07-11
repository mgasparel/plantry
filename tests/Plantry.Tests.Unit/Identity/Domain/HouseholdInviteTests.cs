using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Identity.Domain;

/// <summary>
/// L1 domain tests for <see cref="HouseholdInvite"/> — the invariants R4 (only pending tokens accept;
/// accept is one-way) and R5 (expiry is checked on accept). Time is driven through a fixed clock so
/// nothing depends on when the suite runs (Gate 10).
/// </summary>
public sealed class HouseholdInviteTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly HouseholdId Household = HouseholdId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly Guid Inviter = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Accepter = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static HouseholdInvite Issue(IClock clock, TimeSpan? validity = null) =>
        HouseholdInvite.Issue(Household, "Invitee@Example.com", Inviter, clock, validity);

    [Fact(DisplayName = "Issue creates a pending invite with the household, lower-cased email, inviter, and expiry")]
    public void Issue_Creates_Pending_Invite()
    {
        var invite = Issue(new FixedClock(Now));

        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.Equal(Household, invite.HouseholdId);
        Assert.Equal("invitee@example.com", invite.Email);
        Assert.Equal(Inviter, invite.InvitedByUserId);
        Assert.Equal(Now, invite.CreatedAt);
        Assert.Equal(Now + HouseholdInvite.DefaultValidity, invite.ExpiresAt);
        Assert.Null(invite.AcceptedAt);
    }

    [Fact(DisplayName = "Issue honours an explicit validity window")]
    public void Issue_Honours_Explicit_Validity()
    {
        var invite = Issue(new FixedClock(Now), validity: TimeSpan.FromDays(2));
        Assert.Equal(Now + TimeSpan.FromDays(2), invite.ExpiresAt);
    }

    [Fact(DisplayName = "Issue generates a non-empty, high-entropy token")]
    public void Issue_Generates_Token()
    {
        var invite = Issue(new FixedClock(Now));
        Assert.False(string.IsNullOrWhiteSpace(invite.Token));
        Assert.Equal(64, invite.Token.Length); // 32 bytes hex-encoded
    }

    [Fact(DisplayName = "Two invites issued at the same instant get distinct tokens (R4 uniqueness)")]
    public void Issue_Generates_Distinct_Tokens()
    {
        var clock = new FixedClock(Now);
        Assert.NotEqual(Issue(clock).Token, Issue(clock).Token);
    }

    [Theory(DisplayName = "Issue rejects a missing email")]
    [InlineData("")]
    [InlineData("   ")]
    public void Issue_Rejects_Missing_Email(string email) =>
        Assert.Throws<ArgumentException>(() =>
            HouseholdInvite.Issue(Household, email, Inviter, new FixedClock(Now)));

    // ── Accept (R4 one-way + R5 expiry) ───────────────────────────────────────

    [Fact(DisplayName = "Accept transitions pending → accepted and records accepted_at")]
    public void Accept_Transitions_And_Records_Timestamp()
    {
        var invite = Issue(new FixedClock(Now));
        var acceptClock = new FixedClock(Now + TimeSpan.FromDays(1));

        var result = invite.Accept(Accepter, acceptClock);

        Assert.True(result.IsSuccess);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
        Assert.Equal(acceptClock.UtcNow, invite.AcceptedAt);
        // The joining user is stamped as the audit link (accepted_by_user_id, plantry-bmfg).
        Assert.Equal(Accepter, invite.AcceptedByUserId);
    }

    [Fact(DisplayName = "Accept is one-way: a second accept fails and leaves the first accepted_at intact (R4)")]
    public void Accept_Is_OneWay()
    {
        var invite = Issue(new FixedClock(Now));
        var firstAcceptAt = Now + TimeSpan.FromDays(1);
        invite.Accept(Accepter, new FixedClock(firstAcceptAt));

        var result = invite.Accept(Accepter, new FixedClock(Now + TimeSpan.FromDays(2)));

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.NotPending", result.Error.Code);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
        Assert.Equal(firstAcceptAt, invite.AcceptedAt);
    }

    [Fact(DisplayName = "Accept fails once the invite has expired (R5) and does not transition")]
    public void Accept_Fails_When_Expired()
    {
        var invite = Issue(new FixedClock(Now), validity: TimeSpan.FromDays(7));

        var result = invite.Accept(Accepter, new FixedClock(Now + TimeSpan.FromDays(7)));

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.Expired", result.Error.Code);
        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.Null(invite.AcceptedAt);
    }

    [Fact(DisplayName = "Accept succeeds one tick before expiry (R5 boundary)")]
    public void Accept_Succeeds_Just_Before_Expiry()
    {
        var invite = Issue(new FixedClock(Now), validity: TimeSpan.FromDays(7));
        var justBefore = invite.ExpiresAt - TimeSpan.FromTicks(1);

        Assert.True(invite.Accept(Accepter, new FixedClock(justBefore)).IsSuccess);
    }

    [Fact(DisplayName = "A revoked invite cannot be accepted (R4)")]
    public void Accept_Fails_When_Revoked()
    {
        var invite = Issue(new FixedClock(Now));
        invite.Revoke();

        var result = invite.Accept(Accepter, new FixedClock(Now + TimeSpan.FromDays(1)));

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.NotPending", result.Error.Code);
        Assert.Equal(InviteStatus.Revoked, invite.Status);
    }

    // ── Validate (read-only half of Accept — used by the join GET) ─────────────

    [Fact(DisplayName = "Validate succeeds for a pending, unexpired invite without mutating it")]
    public void Validate_Succeeds_And_Does_Not_Mutate()
    {
        var invite = Issue(new FixedClock(Now));

        var result = invite.Validate(new FixedClock(Now + TimeSpan.FromDays(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.Null(invite.AcceptedAt);
    }

    [Fact(DisplayName = "Validate fails for an expired invite (R5) without mutating it")]
    public void Validate_Fails_When_Expired()
    {
        var invite = Issue(new FixedClock(Now), validity: TimeSpan.FromDays(7));

        var result = invite.Validate(new FixedClock(Now + TimeSpan.FromDays(7)));

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.Expired", result.Error.Code);
        Assert.Equal(InviteStatus.Pending, invite.Status);
    }

    [Fact(DisplayName = "Validate fails for an already-accepted invite (R4) without mutating it")]
    public void Validate_Fails_When_Accepted()
    {
        var invite = Issue(new FixedClock(Now));
        invite.Accept(Accepter, new FixedClock(Now + TimeSpan.FromDays(1)));

        var result = invite.Validate(new FixedClock(Now + TimeSpan.FromDays(2)));

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.NotPending", result.Error.Code);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
    }

    [Fact(DisplayName = "Validate fails for a revoked invite (R4)")]
    public void Validate_Fails_When_Revoked()
    {
        var invite = Issue(new FixedClock(Now));
        invite.Revoke();

        var result = invite.Validate(new FixedClock(Now + TimeSpan.FromDays(1)));

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.NotPending", result.Error.Code);
        Assert.Equal(InviteStatus.Revoked, invite.Status);
    }

    [Fact(DisplayName = "IsExpired reflects the expiry boundary")]
    public void IsExpired_Reflects_Boundary()
    {
        var invite = Issue(new FixedClock(Now), validity: TimeSpan.FromDays(7));
        Assert.False(invite.IsExpired(new FixedClock(Now + TimeSpan.FromDays(6))));
        Assert.True(invite.IsExpired(new FixedClock(invite.ExpiresAt)));
    }

    // ── Revoke ─────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Revoke transitions pending → revoked")]
    public void Revoke_Transitions()
    {
        var invite = Issue(new FixedClock(Now));

        var result = invite.Revoke();

        Assert.True(result.IsSuccess);
        Assert.Equal(InviteStatus.Revoked, invite.Status);
    }

    [Fact(DisplayName = "An accepted invite cannot be revoked")]
    public void Revoke_Fails_When_Accepted()
    {
        var invite = Issue(new FixedClock(Now));
        invite.Accept(Accepter, new FixedClock(Now + TimeSpan.FromDays(1)));

        var result = invite.Revoke();

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.NotPending", result.Error.Code);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
    }

    [Fact(DisplayName = "Revoke is idempotent-guarded: a second revoke fails")]
    public void Revoke_Twice_Fails()
    {
        var invite = Issue(new FixedClock(Now));
        invite.Revoke();

        Assert.True(invite.Revoke().IsFailure);
    }
}
