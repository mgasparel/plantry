using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Identity;

/// <summary>
/// L3 integration tests for <see cref="HouseholdInvite"/> persistence and tenancy. The load-bearing
/// assertion (plantry-00v1 acceptance criterion) is that an invite can be resolved by its token with
/// <b>no tenant context</b> — the unauthenticated invitee's accept path — via the household_invites
/// RLS no-context carve-out, while a scoped context sees only its own household's invites. Also proves
/// the unique-token index (R4). The migration applying cleanly is exercised by the fixture booting.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class HouseholdInviteRlsTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
    private HouseholdId _householdA;
    private HouseholdId _householdB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();

        var clock = new FixedClock(Now);
        await using var identityDb = new PlantryIdentityDbContext(BuildOptions(db.ConnectionString));

        var a = Household.Create("Household A", clock);
        var b = Household.Create("Household B", clock);
        _householdA = a.Id;
        _householdB = b.Id;
        await identityDb.Households.AddAsync(a);
        await identityDb.Households.AddAsync(b);
        await identityDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Token lookup works with NO tenant context (the unauthenticated accept path)")]
    public async Task FindByToken_NoTenantContext_ResolvesInvite()
    {
        var token = await SeedInviteAsync(_householdA, "invitee@example.com");

        // App_user connection, interceptor with NO tenant armed: the households/users/invites carve-out
        // exposes all rows so the invitee can be found before any household is known.
        var noTenant = new TenantContext();
        await using var authDb = new PlantryIdentityDbContext(
            BuildOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(noTenant)));

        var invite = await new HouseholdInviteRepository(authDb).FindByTokenAsync(token);

        Assert.NotNull(invite);
        Assert.Equal(_householdA, invite!.HouseholdId);
        Assert.Equal("invitee@example.com", invite.Email);
    }

    [Fact(DisplayName = "A scoped context cannot read another household's invite (RLS backstop)")]
    public async Task FindByToken_ScopedToOtherHousehold_HidesInvite()
    {
        var token = await SeedInviteAsync(_householdA, "invitee@example.com");

        // Household B armed → the RLS policy restricts the invites table to B's rows, so A's invite is
        // invisible even though the repository lifts the EF query filter (IgnoreQueryFilters).
        var tenantB = new TenantContext();
        tenantB.Set(_householdB.Value);
        await using var scopedDb = new PlantryIdentityDbContext(
            BuildOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenantB)));

        var invite = await new HouseholdInviteRepository(scopedDb).FindByTokenAsync(token);

        Assert.Null(invite);
    }

    [Fact(DisplayName = "EF query filter: a household scoped to A cannot list B's invites")]
    public async Task EfFilter_ScopedToA_HidesBInvites()
    {
        await SeedInviteAsync(_householdA, "a@example.com");
        await SeedInviteAsync(_householdB, "b@example.com");

        // Owner connection (RLS bypassed) isolates the app-layer EF query filter alone.
        await using var identityDb = new PlantryIdentityDbContext(BuildOptions(db.ConnectionString));
        identityDb.SetHouseholdId(_householdA.Value);

        var invites = await identityDb.HouseholdInvites.ToListAsync();

        Assert.All(invites, i => Assert.Equal(_householdA, i.HouseholdId));
        Assert.DoesNotContain(invites, i => i.HouseholdId == _householdB);
    }

    [Fact(DisplayName = "The token unique index rejects a duplicate token (R4)")]
    public async Task DuplicateToken_Violates_UniqueIndex()
    {
        var token = await SeedInviteAsync(_householdA, "first@example.com");

        await using var identityDb = new PlantryIdentityDbContext(BuildOptions(db.ConnectionString));
        // Force a colliding token onto a second household's invite via the raw column, bypassing the
        // random generator, to prove the DB unique constraint fires.
        await using var cmd = identityDb.Database.GetDbConnection().CreateCommand();
        await identityDb.Database.OpenConnectionAsync();
        cmd.CommandText =
            "INSERT INTO identity.household_invites " +
            "(id, household_id, email, token, status, invited_by_user_id, created_at, expires_at) " +
            "VALUES (@id, @hid, 'dupe@example.com', @token, 'pending', @uid, @now, @exp)";
        AddParam(cmd, "id", Guid.CreateVersion7());
        AddParam(cmd, "hid", _householdB.Value);
        AddParam(cmd, "token", token);
        AddParam(cmd, "uid", Guid.CreateVersion7());
        AddParam(cmd, "now", Now);
        AddParam(cmd, "exp", Now.AddDays(7));

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> SeedInviteAsync(HouseholdId household, string email)
    {
        var invite = HouseholdInvite.Issue(household, email, Guid.CreateVersion7(), new FixedClock(Now));
        // Owner connection (RLS bypassed) so the seed itself is not subject to the policy under test.
        await using var identityDb = new PlantryIdentityDbContext(BuildOptions(db.ConnectionString));
        await identityDb.HouseholdInvites.AddAsync(invite);
        await identityDb.SaveChangesAsync();
        return invite.Token;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static DbContextOptions<PlantryIdentityDbContext> BuildOptions(
        string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<PlantryIdentityDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
