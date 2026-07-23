using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Background;
using Plantry.Web.Housekeeping;
using Xunit;

namespace Plantry.Tests.Web.Housekeeping;

/// <summary>
/// L4 integration coverage for the T6 SWR population story (plantry-h0qq) that the pure L2 tests
/// (<c>TidyUpBadgeCacheTests</c>, <c>TidyUpBadgeRefresherTests</c>, <c>TidyUpBadgeWarmupTests</c>) can't
/// reach on their own: a real DI scope is needed to prove (1) the refresher's queued work item actually
/// arms tenancy and lands a fresh count via <c>GetTidyUpPageQuery</c>, and (2) the layout/More-hub badge
/// keeps rendering a stale count instead of hiding it.
/// </summary>
public sealed class TidyUpBadgeSwrIntegrationTests
{
    [Fact(DisplayName = "RequestRefreshAsync's queued work item arms tenancy and populates the cache via GetTidyUpPageQuery")]
    public async Task RequestRefresh_WorkItem_ArmsTenancyAndPopulatesCache()
    {
        using var factory = new TidyUpFragmentFactory();
        var household = HouseholdId.From(Guid.Parse("55555555-0000-0000-0000-500000000099"));

        using var scope = factory.Services.CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<TidyUpBadgeRefresher>();
        var badgeCache = scope.ServiceProvider.GetRequiredService<ITidyUpBadgeCache>();

        await refresher.RequestRefreshAsync(household);

        // QueuedHostedService drains asynchronously on its own loop; poll (bounded) rather than assume a
        // fixed delay is enough — the same pattern other background-queue coverage in this suite would use.
        var snapshot = await PollUntilFreshAsync(badgeCache, household);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsFresh);
        Assert.Equal(1, snapshot.Count); // exactly TidyUpFragmentFactory's one seeded finding
    }

    [Fact(DisplayName = "Stale cache entry still renders the badge count on both the sidebar and the More hub (SWR never blinks off)")]
    public async Task StaleCacheEntry_StillRendersBadgeCount()
    {
        using var factory = new StaleBadgeFragmentFactory();
        var client = factory.CreateAuthClient();

        var html = await (await client.GetAsync("/More")).Content.ReadAsStringAsync();

        AssertVisibleBadge(html, "sidebar-tidyup-badge", StaleBadgeFragmentFactory.StaleCount);
        AssertVisibleBadge(html, "more-tidyup-badge", StaleBadgeFragmentFactory.StaleCount);
    }

    [Fact(DisplayName = "A page render with a cold badge cache requests exactly one background refresh (layout + More-hub both trigger; single-flight collapses to one)")]
    public async Task ColdCache_PageRender_RequestsExactlyOneRefresh()
    {
        // Both _Layout.cshtml and More/Index.cshtml.cs skip the miss/stale-triggered refresh request
        // under the "Testing" environment (see the comment in _Layout.cshtml) so the many other
        // WebApplicationFactory<Program> suites that substitute a counting IBackgroundTaskQueue fake
        // aren't polluted by an incidental enqueue on every render. This factory uses a different host
        // name specifically so that gate does not apply, proving the wiring itself actually fires.
        using var factory = new BadgeWiringProofFactory();
        var client = factory.CreateAuthClient();

        await client.GetAsync("/More");

        Assert.Equal(1, factory.Queue.EnqueueCount);
    }

    private static async Task<TidyUpBadgeSnapshot?> PollUntilFreshAsync(ITidyUpBadgeCache cache, HouseholdId household)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        TidyUpBadgeSnapshot? snapshot;
        do
        {
            snapshot = await cache.TryGetAsync(household);
            if (snapshot is { IsFresh: true }) return snapshot;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);

        return snapshot;
    }

    private static void AssertVisibleBadge(string html, string targetId, int expectedCount)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, $"<span id=\"{targetId}\"[^>]*>{expectedCount}<");
        Assert.True(match.Success, $"Expected a visible badge span id=\"{targetId}\" showing {expectedCount} in the response.");
        Assert.DoesNotContain($"id=\"{targetId}\" style=\"display:none\"", html);
    }
}

/// <summary>
/// L4 WebApplicationFactory that stubs <see cref="ITidyUpBadgeCache"/> to always return a stale snapshot
/// for a fixed household, so the badge render path can be proven to keep showing the count instead of
/// hiding it — without waiting out the real ~10 minute TTL. Reuses <see cref="TidyUpFragmentFactory"/>'s
/// single-finding detector fake and in-memory dismissal repository so no Postgres is touched.
/// </summary>
public sealed class StaleBadgeFragmentFactory : WebApplicationFactory<Program>
{
    public static readonly Guid HouseholdId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000003");
    public const int StaleCount = 5;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IDismissalRepository>();
            services.AddSingleton<IDismissalRepository>(new FakeDismissalRepository());
            services.RemoveAll<IProblemDetector>();
            services.AddSingleton<IProblemDetector>(new FakeProblemDetector(TidyUpFragmentFactory.TestFinding));

            services.RemoveAll<ITidyUpBadgeCache>();
            services.AddSingleton<ITidyUpBadgeCache>(new StaleTidyUpBadgeCache(HouseholdId, StaleCount));
        });
    }

    public HttpClient CreateAuthClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    /// <summary>Always reports a stale (TTL-expired) snapshot for <paramref name="householdId"/> — SetAsync/InvalidateAsync are no-ops so a background refresh triggered mid-test can't change the fixture under it.</summary>
    private sealed class StaleTidyUpBadgeCache(Guid householdId, int count) : ITidyUpBadgeCache
    {
        public Task<TidyUpBadgeSnapshot?> TryGetAsync(HouseholdId id, CancellationToken ct = default) =>
            Task.FromResult(id.Value == householdId ? new TidyUpBadgeSnapshot(count, IsFresh: false) : null);

        public Task SetAsync(HouseholdId id, int c, CancellationToken ct = default) => Task.CompletedTask;

        public Task InvalidateAsync(HouseholdId id, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>
/// L4 WebApplicationFactory that proves the miss/stale-triggered refresh wiring itself (_Layout.cshtml /
/// More/Index.cshtml.cs actually calling <see cref="TidyUpBadgeRefresher.RequestRefreshAsync"/>) — hosted
/// under a name other than "Testing" so the test-suite-protecting gate described in _Layout.cshtml does
/// not suppress it. The real <see cref="ITidyUpBadgeCache"/> singleton is left in place (starts cold for
/// a fresh household in a fresh factory) and <see cref="IBackgroundTaskQueue"/> is replaced with a
/// counting fake that never drains, so the recompute work item never actually runs (no Postgres needed).
/// <see cref="IHouseholdRepository"/> is also replaced with an empty fake: the same "not Testing" gate
/// that un-suppresses the request-level trigger also un-suppresses the startup warmup hook
/// (<c>TidyUpBadgeWarmup</c>), which would otherwise enumerate whatever households happen to sit in
/// whatever local Postgres this process can reach — nondeterministic and irrelevant to what this factory
/// is proving.
/// </summary>
public sealed class BadgeWiringProofFactory : WebApplicationFactory<Program>
{
    public static readonly Guid HouseholdId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000004");
    public CountingBackgroundTaskQueue Queue { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Deliberately NOT "Testing" — see the class doc comment above.
        builder.UseEnvironment("Testing-BadgeWiringProof");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IBackgroundTaskQueue>();
            services.AddSingleton<IBackgroundTaskQueue>(Queue);

            services.RemoveAll<Plantry.Identity.Domain.IHouseholdRepository>();
            services.AddSingleton<Plantry.Identity.Domain.IHouseholdRepository>(new EmptyHouseholdRepository());
        });
    }

    public HttpClient CreateAuthClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    /// <summary>Neutralizes the startup warmup sweep (see the class doc comment) — always reports zero households.</summary>
    private sealed class EmptyHouseholdRepository : Plantry.Identity.Domain.IHouseholdRepository
    {
        public Task<Plantry.Identity.Domain.Household?> FindAsync(HouseholdId id, CancellationToken ct = default) =>
            Task.FromResult<Plantry.Identity.Domain.Household?>(null);

        public Task<IReadOnlyList<HouseholdId>> ListAllIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HouseholdId>>([]);

        public Task AddAsync(Plantry.Identity.Domain.Household household, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Records enqueue calls without running them. Unlike the pure-unit fakes in
    /// <c>TidyUpBadgeRefresherTests</c>/<c>TidyUpBadgeWarmupTests</c> (which never run inside a real ASP.NET
    /// Core host, so nothing ever calls <see cref="DequeueAsync"/>), this factory boots the real
    /// <c>QueuedHostedService</c> — its drain loop calls <see cref="DequeueAsync"/> in a tight loop, and a
    /// synchronous throw there faults the hosted service's background task, which — per the .NET Generic
    /// Host's default <c>BackgroundServiceExceptionBehavior.StopHost</c> — stops (and disposes) the entire
    /// host almost immediately, surfacing as a confusing <c>ObjectDisposedException</c> on the next request
    /// rather than anything that looks like this fake's fault. <see cref="DequeueAsync"/> must block instead
    /// (mirroring <c>RecipeEditorConversionDeferTests.CapturingBackgroundTaskQueue</c>).
    /// </summary>
    public sealed class CountingBackgroundTaskQueue : IBackgroundTaskQueue
    {
        public int EnqueueCount { get; private set; }

        public ValueTask EnqueueAsync(
            Func<IServiceProvider, CancellationToken, Task> workItem, CancellationToken ct = default)
        {
            EnqueueCount++;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }
    }
}
