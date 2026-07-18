using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration tests proving the <see cref="TagRepository"/> active-only filter
/// and the <see cref="Tag.Archive"/> / <see cref="Tag.Unarchive"/> lifecycle against a real
/// Postgres schema with the <c>archived_at</c> migration applied (plantry-7ju).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class TagRepositoryTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly IClock _clock = SystemClock.Instance;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Active-only listing excludes archived tags ─────────────────────────────

    [Fact(DisplayName = "ListAllAsync(activeOnly:true) returns only non-archived tags")]
    public async Task ListAllAsync_ActiveOnly_Excludes_Archived()
    {
        var activeTag   = Tag.Create(_household, "Vegetarian", null, _clock);
        var archivedTag = Tag.Create(_household, "Paleo", null, _clock);

        await using (var ctx = NewContext())
        {
            await ctx.Tags.AddAsync(activeTag);
            await ctx.Tags.AddAsync(archivedTag);
            await ctx.SaveChangesAsync();
        }

        // Archive the second tag via the domain and save.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var tag = await repo.GetByIdAsync(archivedTag.Id);
            tag!.Archive(_clock);
            await repo.SaveChangesAsync();
        }

        // Active-only query must return only the active tag.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var active = await repo.ListAllAsync(activeOnly: true);

            Assert.Single(active);
            Assert.Equal("Vegetarian", active[0].Name);
        }

        // Full listing must return both.
        await using (var ctx2 = NewContext())
        {
            var repo = new TagRepository(ctx2);
            var all = await repo.ListAllAsync(activeOnly: false);

            Assert.Equal(2, all.Count);
        }
    }

    // ── ResolveNamesAsync returns archived tags (existing recipe refs render) ───

    [Fact(DisplayName = "ResolveNamesAsync returns name for archived tag so recipe references render")]
    public async Task ResolveNamesAsync_Returns_Archived_Tag_Name()
    {
        var tag = Tag.Create(_household, "Keto", null, _clock);

        await using (var ctx = NewContext())
        {
            await ctx.Tags.AddAsync(tag);
            await ctx.SaveChangesAsync();
        }

        // Archive.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var loaded = await repo.GetByIdAsync(tag.Id);
            loaded!.Archive(_clock);
            await repo.SaveChangesAsync();
        }

        // ResolveNamesAsync must still return the name.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var names = await repo.ResolveNamesAsync([tag.Id]);

            Assert.True(names.ContainsKey(tag.Id));
            Assert.Equal("Keto", names[tag.Id]);
        }
    }

    // ── Migration: archived_at column persists and is nullable ──────────────────

    [Fact(DisplayName = "Tag round-trips with ArchivedAt null (active) and non-null (archived)")]
    public async Task Tag_ArchivedAt_Roundtrips()
    {
        var tag = Tag.Create(_household, "Gluten-Free", null, _clock);

        await using (var ctx = NewContext())
        {
            await ctx.Tags.AddAsync(tag);
            await ctx.SaveChangesAsync();
        }

        // Verify active state.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var loaded = await repo.GetByIdAsync(tag.Id);
            Assert.NotNull(loaded);
            Assert.False(loaded!.IsArchived);
            Assert.Null(loaded.ArchivedAt);
        }

        // Archive.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var loaded = await repo.GetByIdAsync(tag.Id);
            loaded!.Archive(_clock);
            await repo.SaveChangesAsync();
        }

        // Verify archived state persisted.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var loaded = await repo.GetByIdAsync(tag.Id);
            Assert.NotNull(loaded);
            Assert.True(loaded!.IsArchived);
            Assert.NotNull(loaded.ArchivedAt);
        }

        // Unarchive.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var loaded = await repo.GetByIdAsync(tag.Id);
            loaded!.Unarchive(_clock);
            await repo.SaveChangesAsync();
        }

        // Verify restored.
        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var loaded = await repo.GetByIdAsync(tag.Id);
            Assert.NotNull(loaded);
            Assert.False(loaded!.IsArchived);
            Assert.Null(loaded.ArchivedAt);
        }
    }

    // ── RLS isolation: another household's tags are invisible ─────────────────────

    [Fact(DisplayName = "ListAllAsync does not return tags belonging to another household")]
    public async Task ListAllAsync_Does_Not_Leak_Across_Households()
    {
        var otherHousehold = HouseholdId.New();
        var myTag    = Tag.Create(_household, "My Tag",    null, _clock);
        var otherTag = Tag.Create(otherHousehold, "Other Tag", null, _clock);

        // Seed both using a context scoped to the superuser (no query filter).
        var opts = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        await using (var superCtx = new RecipesDbContext(opts))
        {
            await superCtx.Tags.AddAsync(myTag);
            await superCtx.Tags.AddAsync(otherTag);
            await superCtx.SaveChangesAsync();
        }

        // Query as _household — must only see myTag.
        await using var ctx = NewContext();
        var repo = new TagRepository(ctx);
        var visible = await repo.ListAllAsync();

        Assert.Single(visible);
        Assert.Equal("My Tag", visible[0].Name);
    }

    // ── ResolveExistingIdsAsync (batched existence check for AuthorRecipe) ───────

    [Fact(DisplayName = "ResolveExistingIdsAsync returns existing (incl. archived) ids and omits unknown ids")]
    public async Task ResolveExistingIdsAsync_Returns_Existing_Including_Archived()
    {
        var activeTag   = Tag.Create(_household, "Vegan", null, _clock);
        var archivedTag = Tag.Create(_household, "Whole30", null, _clock);

        await using (var ctx = NewContext())
        {
            await ctx.Tags.AddAsync(activeTag);
            await ctx.Tags.AddAsync(archivedTag);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var repo = new TagRepository(ctx);
            var loaded = await repo.GetByIdAsync(archivedTag.Id);
            loaded!.Archive(_clock);
            await repo.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var resolved = await new TagRepository(ctx)
                .ResolveExistingIdsAsync([activeTag.Id, archivedTag.Id, TagId.New()]);

            // Proves EF translates List.Contains over the value-converted TagId column to a SQL IN (...);
            // the archived tag still resolves (an applied-then-archived tag must not silently vanish).
            Assert.Equal(new HashSet<TagId> { activeTag.Id, archivedTag.Id }, resolved);
        }
    }

    [Fact(DisplayName = "ResolveExistingIdsAsync does not resolve another household's tag")]
    public async Task ResolveExistingIdsAsync_Does_Not_Leak_Across_Households()
    {
        var otherHousehold = HouseholdId.New();
        var myTag    = Tag.Create(_household, "Mine", null, _clock);
        var otherTag = Tag.Create(otherHousehold, "Theirs", null, _clock);

        // Seed both via a superuser context (no household query filter).
        var opts = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        await using (var superCtx = new RecipesDbContext(opts))
        {
            await superCtx.Tags.AddAsync(myTag);
            await superCtx.Tags.AddAsync(otherTag);
            await superCtx.SaveChangesAsync();
        }

        // Query scoped to _household — the foreign tag id must not resolve.
        await using var ctx = NewContext();
        var resolved = await new TagRepository(ctx).ResolveExistingIdsAsync([myTag.Id, otherTag.Id]);

        Assert.Equal(new HashSet<TagId> { myTag.Id }, resolved);
        Assert.DoesNotContain(otherTag.Id, resolved);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private RecipesDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<RecipesDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var ctx = new RecipesDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
