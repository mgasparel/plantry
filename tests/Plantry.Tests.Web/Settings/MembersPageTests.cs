using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Settings;

/// <summary>
/// L4 fragment tests for the /Settings/Members page (plantry-3tvb). Runs the REAL
/// <see cref="HouseholdInviteService"/> over a tenant-scoped in-memory <see cref="IHouseholdInviteRepository"/>
/// (mirroring the EF household query filter) plus a fake <see cref="IHouseholdDirectory"/>, so the page's
/// issue/revoke/list behaviour is exercised end-to-end through the application layer — no Postgres touched.
/// Verifies: the roster + pending invites render; issuing an invite returns the _InvitesList fragment with a
/// copyable /Account/Join?token=… link (no reload); revoke removes the invite; household isolation.
/// </summary>
[Trait("Category", "Web")]
public sealed class MembersPageTests : IClassFixture<MembersFragmentFactory>
{
    private readonly MembersFragmentFactory _factory;

    public MembersPageTests(MembersFragmentFactory factory) => _factory = factory;

    [Fact(DisplayName = "GET /Settings/Members renders the member roster and a pending invite with a join link")]
    public async Task Get_Page_Renders_Members_And_Invites()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/Settings/Members");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Roster
        Assert.Contains("Alice", html);
        Assert.Contains("member-list", html);
        // Pending invites card + seeded invite
        Assert.Contains("Pending invites", html);
        Assert.Contains("pending@example.com", html);
        // Copyable join link is present and carries the token
        Assert.Contains("/Account/Join?token=", html);
        Assert.Contains(MembersFixture.PendingA.Token, html);
    }

    [Fact(DisplayName = "POST Invite returns the _InvitesList fragment with the new invitee and a copyable join link")]
    public async Task Post_Invite_Returns_Fragment_With_Join_Link()
    {
        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("email", "newbie@example.com"),
        ]);

        var response = await client.PostAsync("/Settings/Members?handler=Invite", content);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("newbie@example.com", html);
        // The freshly-issued invite's join URL is rendered — the "copyable join URL without reload" AC.
        var issued = _factory.Store.Items.Single(i => i.Email == "newbie@example.com");
        Assert.Contains($"/Account/Join?token={issued.Token}", html);
        Assert.Contains("Copy link", html);
    }

    [Fact(DisplayName = "POST Invite with a blank email returns the fragment with an error banner")]
    public async Task Post_Invite_Blank_Email_Returns_Error()
    {
        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("email", "   "),
        ]);

        var response = await client.PostAsync("/Settings/Members?handler=Invite", content);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("alert--danger", html);
        Assert.Contains("required", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "POST Revoke removes the invite from the pending list")]
    public async Task Post_Revoke_Removes_Invite()
    {
        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        // Issue a dedicated invite so we don't disturb the shared seed, then revoke it.
        var issueContent = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("email", "revokeme@example.com"),
        ]);
        var issueResponse = await client.PostAsync("/Settings/Members?handler=Invite", issueContent);
        issueResponse.EnsureSuccessStatusCode();
        Assert.Contains("revokeme@example.com", await issueResponse.Content.ReadAsStringAsync());

        var inviteId = _factory.Store.Items.Single(i => i.Email == "revokeme@example.com").Id.Value;

        var revokeContent = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
        ]);
        var revokeResponse = await client.PostAsync(
            $"/Settings/Members?handler=Revoke&id={inviteId}", revokeContent);

        revokeResponse.EnsureSuccessStatusCode();
        var html = await revokeResponse.Content.ReadAsStringAsync();

        // The revoked invite is gone from the pending list (no longer pending).
        Assert.DoesNotContain("revokeme@example.com", html);
        Assert.Equal(InviteStatus.Revoked, _factory.Store.Items.Single(i => i.Email == "revokeme@example.com").Status);
    }

    [Fact(DisplayName = "GET /Settings/Members does not show another household's invite (isolation)")]
    public async Task Get_Page_Does_Not_Show_OtherHousehold_Invite()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/Settings/Members");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("pending@example.com", html);
        Assert.DoesNotContain("other@example.com", html);
    }

    [Fact(DisplayName = "Unauthenticated GET /Settings/Members returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Settings/Members");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            MembersFixture.HouseholdId.ToString());
        return client;
    }

    private static async Task<string> FetchAntiforgeryTokenAsync(HttpClient client)
    {
        var pageHtml = await (await client.GetAsync("/Settings/Members")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            pageHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Members page.");
        return match.Groups[1].Value;
    }
}

/// <summary>Fixture data for the Members L4 tests.</summary>
public static class MembersFixture
{
    public static readonly Guid HouseholdId    = Guid.Parse("cccccccc-0001-0000-0000-000000000001");
    public static readonly Guid OtherHousehold = Guid.Parse("cccccccc-0002-0000-0000-000000000001");

    // Matches the NameIdentifier claim minted by TestAuthHandler, so "invited by" resolves to Alice.
    public static readonly Guid InviterUserId  = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    public static readonly HouseholdUser MemberAlice = new(InviterUserId.ToString(), "Alice");

    public static readonly HouseholdInvite PendingA = HouseholdInvite.Issue(
        Plantry.SharedKernel.HouseholdId.From(HouseholdId), "pending@example.com", InviterUserId, Clock);

    public static readonly HouseholdInvite PendingOther = HouseholdInvite.Issue(
        Plantry.SharedKernel.HouseholdId.From(OtherHousehold), "other@example.com", InviterUserId, Clock);

    public static List<HouseholdInvite> BuildSeed() => [PendingA, PendingOther];
}

/// <summary>Shared, mutable in-memory invite store — writes from one request are visible to the next.</summary>
public sealed class InvitesStore
{
    public List<HouseholdInvite> Items { get; } = MembersFixture.BuildSeed();
}

/// <summary>
/// Tenant-scoped in-memory <see cref="IHouseholdInviteRepository"/> for the Members L4 tests.
/// Scopes id lookups and the pending list to the ambient household (mirroring the EF household query
/// filter in <c>PlantryIdentityDbContext</c>); the token lookup is deliberately unscoped (the no-context
/// accept path).
/// </summary>
public sealed class FakeInviteRepo(InvitesStore store, ITenantContext tenant) : IHouseholdInviteRepository
{
    private Plantry.SharedKernel.HouseholdId? Ambient =>
        tenant.HouseholdId is { } g ? Plantry.SharedKernel.HouseholdId.From(g) : null;

    public Task AddAsync(HouseholdInvite invite, CancellationToken ct = default)
    {
        store.Items.Add(invite);
        return Task.CompletedTask;
    }

    public Task<HouseholdInvite?> FindByIdAsync(HouseholdInviteId id, CancellationToken ct = default)
    {
        var hid = Ambient;
        return Task.FromResult(store.Items.SingleOrDefault(i => i.Id == id && (hid is null || i.HouseholdId == hid)));
    }

    public Task<HouseholdInvite?> FindByTokenAsync(string token, CancellationToken ct = default) =>
        Task.FromResult(store.Items.SingleOrDefault(i => i.Token == token));

    public Task<IReadOnlyList<HouseholdInvite>> ListPendingAsync(CancellationToken ct = default)
    {
        var hid = Ambient;
        var query = store.Items.Where(i => i.Status == InviteStatus.Pending);
        if (hid is not null) query = query.Where(i => i.HouseholdId == hid);
        return Task.FromResult<IReadOnlyList<HouseholdInvite>>(query.OrderByDescending(i => i.CreatedAt).ToList());
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Fake <see cref="IHouseholdDirectory"/> — returns Alice for the test household, nothing otherwise.</summary>
public sealed class FakeHouseholdDirectory(ITenantContext tenant) : IHouseholdDirectory
{
    public Task<IReadOnlyList<HouseholdUser>> ListMembersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<HouseholdUser> members =
            tenant.HouseholdId == MembersFixture.HouseholdId ? [MembersFixture.MemberAlice] : [];
        return Task.FromResult(members);
    }
}

/// <summary>
/// L4 WebApplicationFactory for /Settings/Members. Replaces the invite repository with a tenant-scoped
/// in-memory fake (the real HouseholdInviteService runs over it) and the household directory with a fake —
/// no database needed. The shared <see cref="InvitesStore"/> keeps write state visible across requests.
/// </summary>
public sealed class MembersFragmentFactory : WebApplicationFactory<Program>
{
    public InvitesStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();

            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Tenant-scoped in-memory invite repo backed by the shared store; the real
            // HouseholdInviteService (already registered) picks it up.
            services.RemoveAll<IHouseholdInviteRepository>();
            services.AddSingleton(Store);
            services.AddScoped<IHouseholdInviteRepository>(sp =>
                new FakeInviteRepo(sp.GetRequiredService<InvitesStore>(), sp.GetRequiredService<ITenantContext>()));

            // Fake household directory (avoids the ASP.NET Identity UserManager dependency).
            services.RemoveAll<IHouseholdDirectory>();
            services.AddScoped<IHouseholdDirectory>(sp =>
                new FakeHouseholdDirectory(sp.GetRequiredService<ITenantContext>()));
        });
    }
}
