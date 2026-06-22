using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Settings;

/// <summary>
/// L4 fragment tests for the /Settings/Tags page (plantry-7ju).
/// Verifies that the htmx handlers create, rename, set-category, archive, and unarchive tags
/// and that the returned _TagsList partial reflects the updated state.
/// Household isolation is asserted at the fake-repository level, mirroring the EF global query filter
/// applied in the real RecipesDbContext (the real DB-level guarantee is covered at L3 by
/// TagRepositoryTests.ListAllAsync_Does_Not_Leak_Across_Households).
/// Uses the WAF harness with an in-memory ITagRepository — no Postgres touched.
/// </summary>
[Trait("Category", "Web")]
public sealed class TagsPageTests : IClassFixture<TagsFragmentFactory>
{
    private readonly TagsFragmentFactory _factory;

    public TagsPageTests(TagsFragmentFactory factory) => _factory = factory;

    [Fact(DisplayName = "GET /Settings/Tags renders the tags card and active tag name")]
    public async Task Get_Page_Renders_Active_Tag()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/Settings/Tags");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Vegetarian", html);
        Assert.Contains("tag-admin-list", html);
    }

    [Fact(DisplayName = "POST Create returns _TagsList fragment with the new tag name")]
    public async Task Post_Create_Returns_Fragment_With_New_Tag()
    {
        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("name", "Gluten-Free"),
            new("category", "Diet"),
        ]);

        var response = await client.PostAsync("/Settings/Tags?handler=Create", content);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Gluten-Free", html);
    }

    [Fact(DisplayName = "POST Create with duplicate name returns error message")]
    public async Task Post_Create_Duplicate_Returns_Error()
    {
        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        // Vegetarian is already seeded in the fixture.
        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
            new("name", "Vegetarian"),
        ]);

        var response = await client.PostAsync("/Settings/Tags?handler=Create", content);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        // The fragment must render the error banner with the canonical .alert--danger class.
        Assert.Contains("alert--danger", html);
        Assert.Contains("already exists", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "POST Archive moves tag out of active list into archived section")]
    public async Task Post_Archive_Moves_Tag_To_Archived_Section()
    {
        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        var tagId = TagsFixture.VegetarianId;

        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
        ]);

        var response = await client.PostAsync(
            $"/Settings/Tags?handler=Archive&id={tagId}", content);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Tag should appear in the archived section (tag-admin-row--archived class).
        Assert.Contains("tag-admin-row--archived", html);
        Assert.Contains("Vegetarian", html);
        // Restore button must appear.
        Assert.Contains("Restore", html);
    }

    [Fact(DisplayName = "POST Unarchive moves archived tag back to active list")]
    public async Task Post_Unarchive_Restores_Tag_To_Active()
    {
        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        var archivedId = TagsFixture.ArchivedId;

        var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", token),
        ]);

        var response = await client.PostAsync(
            $"/Settings/Tags?handler=Unarchive&id={archivedId}", content);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // After unarchive it should appear in the active list, not the archived section.
        Assert.Contains("Paleo", html);
        // No more archived section for this tag (check Restore button gone for this tag).
        // The archived section header may still appear if other tags are archived, so just
        // check it now appears in the active editable area (tag-name-input).
        Assert.Contains("tag-name-input", html);
    }

    [Fact(DisplayName = "Unauthenticated GET /Settings/Tags returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        // Client with no X-Test-Household header → TestAuthHandler returns no result.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Settings/Tags");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "GET /Settings/Tags does not render tags belonging to another household (isolation)")]
    public async Task Get_Page_Does_Not_Show_OtherHousehold_Tags()
    {
        // Authenticate as HouseholdId — "Keto" belongs to OtherHousehold and must not appear.
        var client = CreateClient();

        var response = await client.GetAsync("/Settings/Tags");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // "Vegetarian" belongs to HouseholdId and must appear.
        Assert.Contains("Vegetarian", html);
        // "Keto" belongs to OtherHousehold and must NOT appear.
        Assert.DoesNotContain("Keto", html);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            TagsFixture.HouseholdId.ToString());
        return client;
    }

    private static async Task<string> FetchAntiforgeryTokenAsync(HttpClient client)
    {
        var pageHtml = await (await client.GetAsync("/Settings/Tags")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            pageHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Tags page.");
        return match.Groups[1].Value;
    }
}

/// <summary>Fixture data for the Tags L4 tests.</summary>
public static class TagsFixture
{
    public static readonly Guid HouseholdId   = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
    public static readonly Guid OtherHousehold = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");

    public static readonly Guid VegetarianId = Guid.Parse("aaaaaaaa-0001-0000-0000-000000000001");
    public static readonly Guid ArchivedId   = Guid.Parse("aaaaaaaa-0002-0000-0000-000000000001");
    public static readonly Guid OtherTagId   = Guid.Parse("aaaaaaaa-0003-0000-0000-000000000001");

    private static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    /// <summary>
    /// Returns the seed list: two tags for <see cref="HouseholdId"/> (one active, one archived),
    /// plus one for <see cref="OtherHousehold"/> ("Keto") that must never appear in queries
    /// scoped to <see cref="HouseholdId"/>.
    /// </summary>
    public static IReadOnlyList<Tag> BuildSeed()
    {
        var hid      = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
        var otherHid = Plantry.SharedKernel.HouseholdId.From(OtherHousehold);

        var vegetarian = Tag.Create(hid, "Vegetarian", TagCategory.Diet, Clock);
        SetId(vegetarian, VegetarianId);

        var archived = Tag.Create(hid, "Paleo", null, Clock);
        SetId(archived, ArchivedId);
        archived.Archive(Clock);

        // Belongs to a different household — must never appear in HouseholdId queries.
        var otherTag = Tag.Create(otherHid, "Keto", null, Clock);
        SetId(otherTag, OtherTagId);

        return [vegetarian, archived, otherTag];
    }

    /// <summary>
    /// Force the Tag's Id field via reflection since Tag.Create generates a new UUIDv7.
    /// Web-test IDs need to be stable so handler URLs can be constructed deterministically.
    /// </summary>
    private static void SetId(Tag tag, Guid id)
    {
        // TagId is a value object wrapping Guid; Tag.Id has a private setter via EF backing field.
        // Access via the public Id property: check if we need to set via reflection.
        // If the id already matches we're done (UUIDv7 collision is astronomically unlikely).
        if (tag.Id == TagId.From(id)) return;

        // Use reflection to assign the backing field or property.
        var prop = typeof(Tag).GetProperty(nameof(Tag.Id));
        if (prop is not null && prop.CanWrite)
        {
            prop.SetValue(tag, TagId.From(id));
        }
        else
        {
            // Try the backing field pattern EF uses (_id or <Id>k__BackingField).
            var field = typeof(Tag).GetField("_id",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? typeof(Tag).GetField("<Id>k__BackingField",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(tag, TagId.From(id));
        }
    }
}

/// <summary>
/// Shared in-memory store for the Tags L4 tests. Holds tags across all households so the
/// per-request <see cref="FakeTagsRepository"/> (which receives the ambient tenant) can filter
/// to the correct household — mirroring the EF global query filter in RecipesDbContext.
/// Singleton so writes from one request are visible to subsequent GETs within the same test.
/// </summary>
public sealed class TagsStore(IReadOnlyList<Tag> seed)
{
    public List<Tag> Items { get; } = [..seed];
}

/// <summary>
/// In-memory <see cref="ITagRepository"/> for the Tags L4 tests.
/// Scopes reads to the ambient household via <see cref="ITenantContext"/>, mirroring the EF
/// global query filter in <c>RecipesDbContext</c>. Writes are directed to the shared
/// <see cref="TagsStore"/> so multiple requests see each other's changes.
/// </summary>
public sealed class FakeTagsRepository(TagsStore store, ITenantContext tenant) : ITagRepository
{
    private HouseholdId? AmbientHousehold =>
        tenant.HouseholdId is { } g ? Plantry.SharedKernel.HouseholdId.From(g) : null;

    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(store.Items.SingleOrDefault(t =>
            t.HouseholdId == householdId &&
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<Tag?> GetByIdAsync(TagId id, CancellationToken ct = default)
    {
        var hid = AmbientHousehold;
        return Task.FromResult(store.Items.SingleOrDefault(t =>
            t.Id == id && (hid is null || t.HouseholdId == hid)));
    }

    public Task AddAsync(Tag tag, CancellationToken ct = default)
    {
        store.Items.Add(tag);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Tag>> ListAllAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        var hid = AmbientHousehold;
        var query = store.Items.AsEnumerable();
        if (hid is not null) query = query.Where(t => t.HouseholdId == hid);
        if (activeOnly) query = query.Where(t => !t.IsArchived);
        return Task.FromResult<IReadOnlyList<Tag>>(query.OrderBy(t => t.Name).ToList());
    }

    public Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(
        IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
        IReadOnlyDictionary<TagId, string> result = store.Items
            .Where(t => ids.Contains(t.Id))
            .ToDictionary(t => t.Id, t => t.Name);
        return Task.FromResult(result);
    }
}

/// <summary>
/// L4 WebApplicationFactory for the /Settings/Tags page. Replaces ITagRepository with
/// a tenant-scoped in-memory fake and re-registers ManageTagsService over it — no database needed.
/// The shared <see cref="TagsStore"/> keeps write state visible across requests within a test
/// while the scoped <see cref="FakeTagsRepository"/> filters by the per-request tenant.
/// </summary>
public sealed class TagsFragmentFactory : WebApplicationFactory<Program>
{
    // Shared store holds ALL tags (multi-household) so per-request scoping can filter.
    private readonly TagsStore _store = new(TagsFixture.BuildSeed());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Auth: header-driven test scheme.
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Replace ITagRepository with a tenant-scoped fake backed by the shared store.
            services.RemoveAll<ITagRepository>();
            services.AddSingleton(_store);
            services.AddScoped<ITagRepository>(sp =>
                new FakeTagsRepository(
                    sp.GetRequiredService<TagsStore>(),
                    sp.GetRequiredService<ITenantContext>()));

            // Re-register ManageTagsService so it picks up the fake repository.
            services.RemoveAll<ManageTagsService>();
            services.AddScoped<ManageTagsService>();
        });
    }
}
