using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plantry.Identity.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Preferences;

/// <summary>
/// L4 fragment tests for the /Settings/Preferences page — OOB swap behaviour (plantry-ema).
/// Verifies that OnPostStanceAsync returns a multi-fragment response containing:
///   • the updated tag-row HTML (primary swap);
///   • the prefs-meta block with hx-swap-oob="outerHTML" (global counter live-update);
///   • the affected category count span with hx-swap-oob="outerHTML" (per-category live-update).
/// Uses the WAF harness with fake services — no Postgres touched.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PreferencesOobFragmentTests : IClassFixture<PreferencesFragmentFactory>
{
    private readonly PreferencesFragmentFactory _factory;

    public PreferencesOobFragmentTests(PreferencesFragmentFactory factory) => _factory = factory;

    [Fact(DisplayName = "POST Stance returns primary tag-row fragment plus prefs-meta OOB fragment")]
    public async Task PostStance_ReturnsTagRowAndPrefsMeta_OobFragment()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            PreferencesFragmentFixture.HouseholdId.ToString());

        // GET the page first to obtain an antiforgery token.
        var pageHtml = await (await client.GetAsync("/Settings/Preferences")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var tagId = PreferencesFragmentFixture.TagId;
        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("tagId", tagId.ToString()),
            new("stance", "Preferred"),
            new("tagName", "Shellfish"),
        ]);

        var response = await client.PostAsync("/Settings/Preferences?handler=Stance", content);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Primary fragment: tag-row for the tag.
        Assert.Contains($"tag-row-{tagId}", html);

        // OOB fragment: prefs-meta block with hx-swap-oob attribute and id=prefs-meta.
        Assert.Contains("id=\"prefs-meta\"", html);
        Assert.Contains("hx-swap-oob", html);

        // OOB fragment: category count span with id=prefs-cat-count-{slug}.
        Assert.Contains("prefs-cat-count-", html);
    }

    [Fact(DisplayName = "POST Stance OOB prefs-meta counter shows updated count (1 of N tags set)")]
    public async Task PostStance_PrefsMeta_ShowsUpdatedCount()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            PreferencesFragmentFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/Settings/Preferences")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("tagId", PreferencesFragmentFixture.TagId.ToString()),
            new("stance", "Required"),
            new("tagName", "Shellfish"),
        ]);

        var response = await client.PostAsync("/Settings/Preferences?handler=Stance", content);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // After setting one stance the prefs-meta counter must render "<b>1</b> of N tags set"
        // inside the OOB fragment — bound to the _PrefsMeta markup so a regression to "0 of" fails.
        Assert.Matches(@"<b>1</b> of \d+ tags set", html);
    }

    [Fact(DisplayName = "POST Reset returns prefs-groups primary swap plus prefs-meta OOB fragment (ADR-013)")]
    public async Task PostReset_CarriesPrefsMeta_OobFragment()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            PreferencesFragmentFixture.HouseholdId.ToString());

        // Set a stance first so there is something to reset.
        var pageHtml = await (await client.GetAsync("/Settings/Preferences")).Content.ReadAsStringAsync();
        var stanceToken = ExtractAntiforgeryToken(pageHtml);
        var stanceContent = new FormUrlEncodedContent([
            new("__RequestVerificationToken", stanceToken),
            new("tagId", PreferencesFragmentFixture.TagId.ToString()),
            new("stance", "Preferred"),
            new("tagName", "Shellfish"),
        ]);
        await client.PostAsync("/Settings/Preferences?handler=Stance", stanceContent);

        // Now reset — re-fetch token from a fresh GET.
        var page2Html = await (await client.GetAsync("/Settings/Preferences")).Content.ReadAsStringAsync();
        var resetToken = ExtractAntiforgeryToken(page2Html);
        var resetContent = new FormUrlEncodedContent([
            new("__RequestVerificationToken", resetToken),
        ]);

        var response = await client.PostAsync("/Settings/Preferences?handler=Reset", resetContent);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // ADR-013 contract: the Reset response must carry the prefs-meta projection so the counter
        // and Reset button disabled state update live without a full page reload.
        OobContract.AssertCarriesProjections(html, "prefs-meta");

        // The prefs-meta counter must show "0 of N tags set" after a reset.
        Assert.Matches(@"<b>0</b> of \d+ tags set", html);

        // The Reset button must be rendered disabled (SetCount==0).
        Assert.Contains("disabled", html);
    }

    [Fact(DisplayName = "POST Reset response does not repaint the whole prefs-groups region as OOB (ADR-013)")]
    public async Task PostReset_PrimarySwap_IsPrefsGroups_NotOobRepaint()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            PreferencesFragmentFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/Settings/Preferences")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);
        var resetContent = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
        ]);

        var response = await client.PostAsync("/Settings/Preferences?handler=Reset", resetContent);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // prefs-groups must appear exactly once as the primary swap target — not duplicated as OOB.
        var count = System.Text.RegularExpressions.Regex.Matches(html, "id=\"prefs-groups\"").Count;
        Assert.Equal(1, count);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }
}

/// <summary>Fixture data for the Preferences L4 OOB fragment tests.</summary>
public static class PreferencesFragmentFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    public static readonly Guid UserId = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
    public static readonly Guid TagId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    public static IReadOnlyList<HouseholdMember> Members =>
        [new HouseholdMember(UserId, "Test User", "TU")];

    public static IReadOnlyList<TagGroup> Tags =>
        [new TagGroup("Protein", 28, [new TagSummary(TagId, "Shellfish", "Protein", 28)])];
}

/// <summary>
/// L4 WebApplicationFactory for the Preferences page. Replaces all Postgres-backed and Identity
/// seams with in-memory fakes — no database needed.
/// </summary>
public sealed class PreferencesFragmentFactory : WebApplicationFactory<Program>
{
    // Shared in-memory preference repository so stance POSTs affect subsequent GETs in same test.
    private readonly FakePrefsRepository _prefsRepo = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            // Auth: header-driven test scheme (same as other L4 tests).
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Replace UserManager<AppUser> with a stub that returns our fixture user.
            services.RemoveAll<UserManager<AppUser>>();
            services.AddSingleton<UserManager<AppUser>>(
                new FakeUserManager(new AppUser { Id = PreferencesFragmentFixture.UserId.ToString() }));

            // Replace the tag reader with the fixture tags.
            services.RemoveAll<ITagReader>();
            services.AddSingleton<ITagReader>(new FakeTagReaderStub(PreferencesFragmentFixture.Tags));

            // Replace the household member reader.
            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(
                new FakeHouseholdMemberReaderStub(PreferencesFragmentFixture.Members));

            // Replace the user preference repository with our shared in-memory fake.
            services.RemoveAll<IUserPreferenceRepository>();
            services.AddSingleton<IUserPreferenceRepository>(_prefsRepo);

            // Re-register SetPreferences so it picks up the fakes.
            services.RemoveAll<SetPreferences>();
            services.AddScoped<SetPreferences>();
        });
    }
}

// ── fakes ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Minimal UserManager stub that bypasses Identity infrastructure and always returns the fixture user.
/// </summary>
public sealed class FakeUserManager(AppUser fixedUser)
    : UserManager<AppUser>(
        new FakeUserStore(),
        null!, null!, null!, null!, null!, null!, null!, null!)
{
    public override Task<AppUser?> GetUserAsync(ClaimsPrincipal principal) =>
        Task.FromResult<AppUser?>(fixedUser);
}

public sealed class FakeUserStore : IUserStore<AppUser>
{
    public Task<IdentityResult> CreateAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
    public Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
    public void Dispose() { }
    public Task<AppUser?> FindByIdAsync(string userId, CancellationToken ct) => Task.FromResult<AppUser?>(null);
    public Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => Task.FromResult<AppUser?>(null);
    public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string> GetUserIdAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.UserName);
    public Task SetNormalizedUserNameAsync(AppUser user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;
    public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken ct) => Task.CompletedTask;
    public Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
}

public sealed class FakePrefsRepository : IUserPreferenceRepository
{
    private UserPreference? _pref;

    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_pref?.UserId == userId ? _pref : null);

    public Task AddAsync(UserPreference pref, CancellationToken ct = default)
    {
        _pref = pref;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeTagReaderStub(IReadOnlyList<TagGroup> groups) : ITagReader
{
    public Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default) =>
        Task.FromResult(groups);
}

public sealed class FakeHouseholdMemberReaderStub(IReadOnlyList<HouseholdMember> members) : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default) =>
        Task.FromResult(members);
}
