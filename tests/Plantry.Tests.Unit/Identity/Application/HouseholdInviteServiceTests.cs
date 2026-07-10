using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Identity.Application;

/// <summary>
/// L1 tests for <see cref="HouseholdInviteService"/> — issue/revoke run under an authenticated
/// household (tenant context), accept runs with NO tenant context and resolves by token. Time is a
/// fixed clock (Gate 10). The fake repository stands in for the tenant-scoped vs. token lookups.
/// </summary>
public sealed class HouseholdInviteServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid HouseholdA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Inviter = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private static HouseholdInviteService Service(FakeInviteRepository repo, Guid? household, IClock? clock = null) =>
        new(repo, new FakeTenantContext(household), clock ?? new FixedClock(Now),
            NullLogger<HouseholdInviteService>.Instance);

    // ── Issue ──────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Issue persists a pending invite scoped to the current household and returns its id")]
    public async Task Issue_Persists_And_Returns_Id()
    {
        var repo = new FakeInviteRepository();
        var result = await Service(repo, HouseholdA).IssueAsync("invitee@example.com", Inviter);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, repo.SaveChangesCalls);
        var invite = Assert.Single(repo.Invites);
        Assert.Equal(result.Value, invite.Id);
        Assert.Equal(HouseholdId.From(HouseholdA), invite.HouseholdId);
        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.Equal(Inviter, invite.InvitedByUserId);
        Assert.False(string.IsNullOrWhiteSpace(invite.Token));
    }

    [Fact(DisplayName = "Issue returns Unauthorized when there is no household in context")]
    public async Task Issue_Requires_Household()
    {
        var repo = new FakeInviteRepository();
        var result = await Service(repo, household: null).IssueAsync("invitee@example.com", Inviter);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Issue rejects a missing email without touching the store")]
    public async Task Issue_Rejects_Missing_Email()
    {
        var repo = new FakeInviteRepository();
        var result = await Service(repo, HouseholdA).IssueAsync("  ", Inviter);

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.EmailRequired", result.Error.Code);
        Assert.Empty(repo.Invites);
    }

    // ── Accept (no tenant context; by token) ───────────────────────────────────

    [Fact(DisplayName = "Accept resolves by token with no tenant context and returns the joined household + email")]
    public async Task Accept_By_Token_Returns_Household()
    {
        var invite = HouseholdInvite.Issue(HouseholdId.From(HouseholdA), "invitee@example.com", Inviter, new FixedClock(Now));
        var repo = new FakeInviteRepository(invite);

        // No tenant context — the unauthenticated invitee path.
        var result = await Service(repo, household: null, clock: new FixedClock(Now + TimeSpan.FromDays(1)))
            .AcceptAsync(invite.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(HouseholdId.From(HouseholdA), result.Value.HouseholdId);
        Assert.Equal("invitee@example.com", result.Value.Email);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Accept returns NotFound for an unknown token")]
    public async Task Accept_Unknown_Token_NotFound()
    {
        var repo = new FakeInviteRepository();
        var result = await Service(repo, household: null).AcceptAsync("deadbeef");

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Accept rejects an expired invite (R5) and does not persist")]
    public async Task Accept_Expired_Fails()
    {
        var invite = HouseholdInvite.Issue(
            HouseholdId.From(HouseholdA), "invitee@example.com", Inviter, new FixedClock(Now), TimeSpan.FromDays(7));
        var repo = new FakeInviteRepository(invite);

        var result = await Service(repo, household: null, clock: new FixedClock(Now + TimeSpan.FromDays(8)))
            .AcceptAsync(invite.Token);

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.Expired", result.Error.Code);
        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Accept rejects an already-accepted token (R4 one-way)")]
    public async Task Accept_AlreadyAccepted_Fails()
    {
        var invite = HouseholdInvite.Issue(HouseholdId.From(HouseholdA), "invitee@example.com", Inviter, new FixedClock(Now));
        invite.Accept(new FixedClock(Now + TimeSpan.FromDays(1)));
        var repo = new FakeInviteRepository(invite);

        var result = await Service(repo, household: null, clock: new FixedClock(Now + TimeSpan.FromDays(2)))
            .AcceptAsync(invite.Token);

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.NotPending", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Accept rejects a blank token")]
    public async Task Accept_Blank_Token_Fails()
    {
        var repo = new FakeInviteRepository();
        var result = await Service(repo, household: null).AcceptAsync("   ");

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.TokenRequired", result.Error.Code);
    }

    // ── Revoke ─────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Revoke transitions a pending invite and persists")]
    public async Task Revoke_Succeeds()
    {
        var invite = HouseholdInvite.Issue(HouseholdId.From(HouseholdA), "invitee@example.com", Inviter, new FixedClock(Now));
        var repo = new FakeInviteRepository(invite);

        var result = await Service(repo, HouseholdA).RevokeAsync(invite.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(InviteStatus.Revoked, invite.Status);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Revoke returns Unauthorized with no household in context")]
    public async Task Revoke_Requires_Household()
    {
        var invite = HouseholdInvite.Issue(HouseholdId.From(HouseholdA), "invitee@example.com", Inviter, new FixedClock(Now));
        var repo = new FakeInviteRepository(invite);

        var result = await Service(repo, household: null).RevokeAsync(invite.Id);

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Revoke returns NotFound when no such invite exists for the active household")]
    public async Task Revoke_NotFound_When_Missing()
    {
        // The tenant-scoped lookup (EF filter + RLS, proven at L3) yields nothing → NotFound. Here the
        // store is simply empty; cross-household revoke isolation itself is asserted in the integration suite.
        var repo = new FakeInviteRepository();

        var result = await Service(repo, HouseholdA).RevokeAsync(HouseholdInviteId.New());

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Revoke surfaces the aggregate's not-pending failure for an accepted invite")]
    public async Task Revoke_Accepted_Fails()
    {
        var invite = HouseholdInvite.Issue(HouseholdId.From(HouseholdA), "invitee@example.com", Inviter, new FixedClock(Now));
        invite.Accept(new FixedClock(Now + TimeSpan.FromDays(1)));
        var repo = new FakeInviteRepository(invite);

        var result = await Service(repo, HouseholdA).RevokeAsync(invite.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("Invite.NotPending", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    // ── doubles ──────────────────────────────────────────────────────────────────

    private sealed class FakeTenantContext(Guid? householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>
    /// In-memory <see cref="IHouseholdInviteRepository"/>. <see cref="FindByTokenAsync"/> is the
    /// cross-tenant (no-context) lookup and <see cref="FindByIdAsync"/> the by-id lookup; the actual
    /// per-household scoping is the EF query filter + Postgres RLS, proven in the L3 integration suite.
    /// </summary>
    private sealed class FakeInviteRepository : IHouseholdInviteRepository
    {
        public List<HouseholdInvite> Invites { get; } = [];
        public int SaveChangesCalls { get; private set; }

        public FakeInviteRepository() { }
        public FakeInviteRepository(HouseholdInvite seeded) => Invites.Add(seeded);

        public Task AddAsync(HouseholdInvite invite, CancellationToken ct = default)
        {
            Invites.Add(invite);
            return Task.CompletedTask;
        }

        public Task<HouseholdInvite?> FindByIdAsync(HouseholdInviteId id, CancellationToken ct = default) =>
            Task.FromResult(Invites.SingleOrDefault(i => i.Id == id));

        public Task<HouseholdInvite?> FindByTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(Invites.SingleOrDefault(i => i.Token == token));

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            SaveChangesCalls++;
            return Task.CompletedTask;
        }
    }
}
