using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 unit tests for <see cref="ManageTagsService"/> — the application service that drives the
/// /Settings/Tags admin page. Covers create, rename, set-category, archive, and unarchive flows.
/// </summary>
public sealed class ManageTagsServiceTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdGuid = Guid.NewGuid();

    // ── Harness ─────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required FakeTagRepository Tags { get; init; }
        public required ManageTagsService Service { get; init; }
    }

    private Harness BuildHarness(bool authenticated = true)
    {
        var tags = new FakeTagRepository();
        var tenant = new FakeTenantContext(authenticated ? _householdGuid : null);
        var service = new ManageTagsService(tags, Clock, tenant, NullLogger<ManageTagsService>.Instance);
        return new Harness { Tags = tags, Service = service };
    }

    private HouseholdId Household => HouseholdId.From(_householdGuid);

    // ── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Adds_Active_Tag_And_Returns_Ok()
    {
        var h = BuildHarness();

        var result = await h.Service.CreateAsync("Vegetarian", TagCategory.Diet);

        Assert.True(result.IsSuccess);
        Assert.Single(h.Tags.Items);
        Assert.Equal("Vegetarian", h.Tags.Items[0].Name);
        Assert.Equal(TagCategory.Diet, h.Tags.Items[0].Category);
        Assert.False(h.Tags.Items[0].IsArchived);
        Assert.Equal(1, h.Tags.SaveChangesCalls);
    }

    [Fact]
    public async Task Create_With_Null_Category_Adds_Tag_Without_Category()
    {
        var h = BuildHarness();

        var result = await h.Service.CreateAsync("Spicy", null);

        Assert.True(result.IsSuccess);
        Assert.Null(h.Tags.Items[0].Category);
    }

    [Fact]
    public async Task Create_Rejects_Duplicate_Name_Case_Insensitive()
    {
        var h = BuildHarness();
        h.Tags.Items.Add(Tag.Create(Household, "Vegan", null, Clock));

        var result = await h.Service.CreateAsync("vegan", null);

        Assert.False(result.IsSuccess);
        Assert.Contains("already exists", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Tags.Items); // no new tag added
        Assert.Equal(0, h.Tags.SaveChangesCalls);
    }

    [Fact]
    public async Task Create_Rejects_Blank_Name()
    {
        var h = BuildHarness();

        var result = await h.Service.CreateAsync("   ", null);

        Assert.False(result.IsSuccess);
        Assert.Empty(h.Tags.Items);
    }

    [Fact]
    public async Task Create_Archived_Tag_Blocks_Reuse_Of_Its_Name()
    {
        var h = BuildHarness();
        var tag = Tag.Create(Household, "Paleo", null, Clock);
        tag.Archive(Clock);
        h.Tags.Items.Add(tag);

        // Name is still reserved by the archived tag.
        var result = await h.Service.CreateAsync("Paleo", null);

        Assert.False(result.IsSuccess);
        Assert.Contains("already exists", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Rename ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_Changes_Name_And_Returns_Ok()
    {
        var h = BuildHarness();
        var tag = Tag.Create(Household, "Veggie", null, Clock);
        h.Tags.Items.Add(tag);

        var result = await h.Service.RenameAsync(tag.Id, "Vegetarian");

        Assert.True(result.IsSuccess);
        Assert.Equal("Vegetarian", h.Tags.Items[0].Name);
        Assert.Equal(1, h.Tags.SaveChangesCalls);
    }

    [Fact]
    public async Task Rename_Allows_Same_Name_On_Same_Tag()
    {
        var h = BuildHarness();
        var tag = Tag.Create(Household, "Vegan", null, Clock);
        h.Tags.Items.Add(tag);

        // Renaming to the same name (case-insensitive) is allowed.
        var result = await h.Service.RenameAsync(tag.Id, "Vegan");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Rename_Rejects_Name_Taken_By_Different_Tag()
    {
        var h = BuildHarness();
        var tag1 = Tag.Create(Household, "Vegan", null, Clock);
        var tag2 = Tag.Create(Household, "Paleo", null, Clock);
        h.Tags.Items.AddRange([tag1, tag2]);

        var result = await h.Service.RenameAsync(tag2.Id, "Vegan");

        Assert.False(result.IsSuccess);
        Assert.Equal("Paleo", tag2.Name); // unchanged
        Assert.Equal(0, h.Tags.SaveChangesCalls);
    }

    [Fact]
    public async Task Rename_Returns_NotFound_For_Unknown_Id()
    {
        var h = BuildHarness();

        var result = await h.Service.RenameAsync(TagId.New(), "Whatever");

        Assert.False(result.IsSuccess);
        Assert.Equal(0, h.Tags.SaveChangesCalls);
    }

    // ── SetCategory ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetCategory_Updates_Category()
    {
        var h = BuildHarness();
        var tag = Tag.Create(Household, "Beef", null, Clock);
        h.Tags.Items.Add(tag);

        var result = await h.Service.SetCategoryAsync(tag.Id, TagCategory.Protein);

        Assert.True(result.IsSuccess);
        Assert.Equal(TagCategory.Protein, h.Tags.Items[0].Category);
        Assert.Equal(1, h.Tags.SaveChangesCalls);
    }

    [Fact]
    public async Task SetCategory_Clears_Category_When_Null()
    {
        var h = BuildHarness();
        var tag = Tag.Create(Household, "Beef", TagCategory.Protein, Clock);
        h.Tags.Items.Add(tag);

        var result = await h.Service.SetCategoryAsync(tag.Id, null);

        Assert.True(result.IsSuccess);
        Assert.Null(h.Tags.Items[0].Category);
    }

    // ── Archive ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archive_Sets_IsArchived_And_Returns_Ok()
    {
        var h = BuildHarness();
        var tag = Tag.Create(Household, "Keto", null, Clock);
        h.Tags.Items.Add(tag);

        var result = await h.Service.ArchiveAsync(tag.Id);

        Assert.True(result.IsSuccess);
        Assert.True(h.Tags.Items[0].IsArchived);
        Assert.Equal(1, h.Tags.SaveChangesCalls);
    }

    [Fact]
    public async Task Archive_Is_Idempotent()
    {
        var h = BuildHarness();
        var tag = Tag.Create(Household, "Keto", null, Clock);
        tag.Archive(Clock);
        h.Tags.Items.Add(tag);

        var result = await h.Service.ArchiveAsync(tag.Id);

        Assert.True(result.IsSuccess);
        Assert.True(h.Tags.Items[0].IsArchived);
    }

    [Fact]
    public async Task Archive_Returns_NotFound_For_Unknown_Id()
    {
        var h = BuildHarness();

        var result = await h.Service.ArchiveAsync(TagId.New());

        Assert.False(result.IsSuccess);
    }

    // ── Unarchive ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unarchive_Restores_Tag_To_Active()
    {
        var h = BuildHarness();
        var tag = Tag.Create(Household, "Keto", null, Clock);
        tag.Archive(Clock);
        h.Tags.Items.Add(tag);

        var result = await h.Service.UnarchiveAsync(tag.Id);

        Assert.True(result.IsSuccess);
        Assert.False(h.Tags.Items[0].IsArchived);
        Assert.Null(h.Tags.Items[0].ArchivedAt);
        Assert.Equal(1, h.Tags.SaveChangesCalls);
    }

    // ── ListAll ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAll_Returns_Both_Active_And_Archived_Tags()
    {
        var h = BuildHarness();
        var active   = Tag.Create(Household, "Active Tag", null, Clock);
        var archived = Tag.Create(Household, "Archived Tag", null, Clock);
        archived.Archive(Clock);
        h.Tags.Items.AddRange([active, archived]);

        var all = await h.Service.ListAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, t => t.Name == "Active Tag" && !t.IsArchived);
        Assert.Contains(all, t => t.Name == "Archived Tag" && t.IsArchived);
    }

    // ── Active-only listing (for picker / preferences) ───────────────────────────

    [Fact]
    public async Task ListAllAsync_ActiveOnly_Excludes_Archived()
    {
        var h = BuildHarness();
        var active   = Tag.Create(Household, "Active", null, Clock);
        var archived = Tag.Create(Household, "Archived", null, Clock);
        archived.Archive(Clock);
        h.Tags.Items.AddRange([active, archived]);

        // Call via repository directly (activeOnly: true).
        var activeOnly = await h.Tags.ListAllAsync(activeOnly: true);

        Assert.Single(activeOnly);
        Assert.Equal("Active", activeOnly[0].Name);
    }

    // ── ResolveNamesAsync includes archived (existing recipe references) ──────────

    [Fact]
    public async Task ResolveNamesAsync_Includes_Archived_Tags()
    {
        var h = BuildHarness();
        var archived = Tag.Create(Household, "Paleo", null, Clock);
        archived.Archive(Clock);
        h.Tags.Items.Add(archived);

        // Resolve by ID — must still return the name even though archived.
        var ids = (IReadOnlyList<TagId>)[archived.Id];
        var resolved = await h.Tags.ResolveNamesAsync(ids);

        Assert.True(resolved.ContainsKey(archived.Id));
        Assert.Equal("Paleo", resolved[archived.Id]);
    }
}
