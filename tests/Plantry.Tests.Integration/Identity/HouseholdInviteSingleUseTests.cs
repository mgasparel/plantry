using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Identity;

/// <summary>
/// L3 single-use enforcement for <see cref="HouseholdInvite"/> under concurrency (plantry-bmfg). Two
/// acceptance criteria are proven against a real Postgres:
/// <list type="bullet">
///   <item>the <c>xmin</c> optimistic-concurrency token makes a second accept that loaded the invite
///     while it was still pending fail closed at the database (deterministic race);</item>
///   <item>two concurrent joins with <b>different</b> emails on the same invite — which slip past the
///     join page's <c>FindByEmail</c> pre-check — create exactly ONE member: the loser's user INSERT
///     rolls back with its transaction, so a single-use invite can never mint two members.</item>
/// </list>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class HouseholdInviteSingleUseTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();

        var clock = new FixedClock(Now);
        await using var identityDb = new PlantryIdentityDbContext(BuildOptions(db.ConnectionString));
        var household = Household.Create("Concurrency Household", clock);
        _household = household.Id;
        await identityDb.Households.AddAsync(household);
        await identityDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Concurrent accept: the second writer loses the xmin race and cannot double-accept (single-use)")]
    public async Task Concurrent_Accept_SecondWriter_FailsClosed_OnXmin()
    {
        var token = await SeedInviteAsync(_household, "invitee@example.com");
        var winner = Guid.CreateVersion7();
        var loser = Guid.CreateVersion7();

        // Two independent connections (two "requests"), each with NO tenant armed — the accept path.
        await using var ctx1 = new PlantryIdentityDbContext(
            BuildOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(new TenantContext())));
        await using var ctx2 = new PlantryIdentityDbContext(
            BuildOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(new TenantContext())));

        var repo1 = new HouseholdInviteRepository(ctx1);
        var repo2 = new HouseholdInviteRepository(ctx2);

        // Both requests load the SAME pending invite (same xmin) before either writes.
        var invite1 = await repo1.FindByTokenAsync(token);
        var invite2 = await repo2.FindByTokenAsync(token);
        Assert.NotNull(invite1);
        Assert.NotNull(invite2);

        var clock = new FixedClock(Now + TimeSpan.FromDays(1));

        // Winner commits first — both the aggregate transition and the guarded UPDATE succeed.
        Assert.True(invite1!.Accept(winner, clock).IsSuccess);
        await repo1.SaveChangesAsync();

        // The loser's in-memory copy still looks pending, so the aggregate transition passes — but the
        // xmin-guarded UPDATE matches zero rows (the winner bumped xmin) and fails closed. The repository
        // surfaces this as the layering-safe ConcurrencyConflictException the service folds to NotPending.
        Assert.True(invite2!.Accept(loser, clock).IsSuccess);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repo2.SaveChangesAsync());

        // The invite is accepted exactly once, stamped with the winner.
        await using var verify = new PlantryIdentityDbContext(BuildOptions(db.ConnectionString));
        verify.SetHouseholdId(_household.Value);
        var persisted = await verify.HouseholdInvites.SingleAsync(i => i.Token == token);
        Assert.Equal(InviteStatus.Accepted, persisted.Status);
        Assert.Equal(winner, persisted.AcceptedByUserId);
    }

    [Fact(DisplayName = "Two concurrent joins with different emails on one invite create exactly one member (loser rolls back)")]
    public async Task Concurrent_Join_DifferentEmails_YieldsSingleMember()
    {
        var token = await SeedInviteAsync(_household, "invitee@example.com");

        await using var provider = BuildJoinServices(db.AppUserConnectionString, Now + TimeSpan.FromDays(1));

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var cmd1 = scope1.ServiceProvider.GetRequiredService<JoinHouseholdCommand>();
        var cmd2 = scope2.ServiceProvider.GetRequiredService<JoinHouseholdCommand>();

        // Different emails, so the join page's FindByEmail pre-check would NOT catch this — the DB
        // single-use guard is the only thing standing between one invite and two members.
        var join1 = cmd1.ExecuteAsync(token, _household, "alice@example.com", "Alice", "testpass1");
        var join2 = cmd2.ExecuteAsync(token, _household, "bob@example.com", "Bob", "testpass1");
        var results = await Task.WhenAll(join1, join2);

        // Exactly one join committed; the other saw the invite gone and rolled back.
        Assert.Equal(1, results.Count(r => r.Outcome == JoinOutcome.Succeeded));
        Assert.Equal(1, results.Count(r => r.Outcome == JoinOutcome.InviteUnavailable));

        var winner = results.Single(r => r.Outcome == JoinOutcome.Succeeded).User!;

        // The household has exactly ONE member — the loser's user INSERT vanished with its transaction.
        await using var verify = new PlantryIdentityDbContext(BuildOptions(db.ConnectionString));
        var members = await verify.Users.Where(u => u.HouseholdId == _household.Value).ToListAsync();
        Assert.Single(members);
        Assert.Equal(winner.Id, members[0].Id);

        // The invite is accepted, stamped with the surviving member (the audit link).
        verify.SetHouseholdId(_household.Value);
        var invite = await verify.HouseholdInvites.SingleAsync(i => i.Token == token);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
        Assert.Equal(Guid.Parse(winner.Id), invite.AcceptedByUserId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> SeedInviteAsync(HouseholdId household, string email)
    {
        var invite = HouseholdInvite.Issue(household, email, Guid.CreateVersion7(), new FixedClock(Now));
        // Owner connection (RLS bypassed) so the seed is not subject to the policy under test.
        await using var identityDb = new PlantryIdentityDbContext(BuildOptions(db.ConnectionString));
        await identityDb.HouseholdInvites.AddAsync(invite);
        await identityDb.SaveChangesAsync();
        return invite.Token;
    }

    // A minimal DI graph mirroring Program.cs's Identity wiring: the shared PlantryIdentityDbContext (so
    // UserManager and the invite repository share a scoped connection — the load-bearing fact behind the
    // single transaction), ASP.NET Identity core with the app's relaxed password rules, and the join command.
    private static ServiceProvider BuildJoinServices(string appUserConnStr, DateTimeOffset now)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(now));
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<HouseholdRlsConnectionInterceptor>();
        services.AddDbContext<PlantryIdentityDbContext>((sp, o) =>
            o.UseNpgsql(appUserConnStr)
                .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
        services.AddIdentityCore<AppUser>(opts =>
            {
                opts.Password.RequireDigit = false;
                opts.Password.RequireLowercase = false;
                opts.Password.RequireNonAlphanumeric = false;
                opts.Password.RequireUppercase = false;
                opts.Password.RequiredLength = 8;
            })
            .AddEntityFrameworkStores<PlantryIdentityDbContext>();
        services.AddScoped<IHouseholdInviteRepository, HouseholdInviteRepository>();
        services.AddScoped<HouseholdInviteService>();
        services.AddScoped<JoinHouseholdCommand>();
        return services.BuildServiceProvider();
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
