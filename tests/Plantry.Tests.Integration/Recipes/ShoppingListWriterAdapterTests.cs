using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;
using Plantry.Shopping.Infrastructure;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Tests.Integration.Shopping;
using Plantry.Web.Recipes;
using Xunit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration test proving the <see cref="ShoppingListWriterAdapter"/> wiring (P2-4a, plantry-gsj):
/// calling SyncSourceContributionAsync routes through Shopping's real SET/sync path, stamps
/// source=recipe + source_ref=recipeId, and applies idempotent SET semantics — re-syncing the same
/// target is a no-op (no drift) and re-syncing a lower target sets the slice down (last-press-wins).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ShoppingListWriterAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private readonly Guid _product1 = Guid.CreateVersion7();
    private readonly Guid _product2 = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _recipeId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed the household's shopping list via the production seeder.
        await using var ctx = NewShoppingDb();
        var seeder = new ShoppingReferenceDataSeeder(ctx, Clock);
        await seeder.SeedAsync(_household);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Source stamping ────────────────────────────────────────────────────────

    [Fact(DisplayName = "L3 — adapter writes items with source=recipe and source_ref=recipeId (contribution model)")]
    public async Task Adapter_Stamps_RecipeSource_And_RecipeId()
    {
        var adapter = BuildAdapter();
        var items = new[] { new ShoppingItem(_product1, 2m, _unitId) };

        await adapter.SyncSourceContributionAsync(items, source: "recipe", sourceRef: _recipeId);

        await using var ctx = NewShoppingDb();
        var list = await ctx.ShoppingLists
            .Include(l => l.Items)
            .ThenInclude(i => i.Contributions)
            .FirstAsync();
        var item = Assert.Single(list.Items);
        Assert.Equal(_product1, item.ProductId);
        Assert.Equal(2m, item.Quantity);
        // Source/SourceRef now live on the contribution (plantry-9scq).
        var contrib = Assert.Single(item.Contributions);
        Assert.Equal(ItemSource.Recipe, contrib.Source);
        Assert.Equal(_recipeId, contrib.SourceRef);
    }

    // ── Reconcile rule (plantry-wxho) — idempotent add-missing ──────────────

    [Fact(DisplayName = "L3 — sync: re-syncing the same recipe shortfall twice leaves one row with unchanged quantity (no drift)")]
    public async Task Sync_SameShortfallTwice_IsIdempotent()
    {
        var adapter = BuildAdapter();

        // First sync — shortfall of 3 units of product1
        await adapter.SyncSourceContributionAsync(
            [new ShoppingItem(_product1, 3m, _unitId)],
            source: "recipe",
            sourceRef: _recipeId);

        // Verify one row exists with qty = 3
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).ThenInclude(i => i.Contributions).FirstAsync();
            Assert.Single(list.Items);
            Assert.Equal(3m, list.Items[0].Quantity);
        }

        // Second sync — same shortfall of 3 — no-op (idempotent SET)
        await adapter.SyncSourceContributionAsync(
            [new ShoppingItem(_product1, 3m, _unitId)],
            source: "recipe",
            sourceRef: _recipeId);

        // Confirm still one row with quantity = 3 (idempotent, not doubled to 6)
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists
                .Include(l => l.Items)
                .ThenInclude(i => i.Contributions)
                .FirstAsync();
            Assert.Single(list.Items);              // NOT two rows
            Assert.Equal(3m, list.Items[0].Quantity); // unchanged — idempotent
            // One contribution (same source/sourceRef → the slice was SET, not appended).
            var contrib = Assert.Single(list.Items[0].Contributions);
            Assert.Equal(ItemSource.Recipe, contrib.Source);
        }
    }

    [Fact(DisplayName = "L3 — sync SET/last-press-wins: a larger target grows the slice, a smaller target sets it back down")]
    public async Task Sync_SetsSliceToLatestTarget()
    {
        var adapter = BuildAdapter();

        // First sync — 2 units on the list
        await adapter.SyncSourceContributionAsync(
            [new ShoppingItem(_product1, 2m, _unitId)],
            source: "recipe",
            sourceRef: _recipeId);

        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).ThenInclude(i => i.Contributions).FirstAsync();
            Assert.Equal(2m, list.Items[0].Quantity);
        }

        // Second sync — target grows to 5 — slice SET to 5 (not stacked to 7)
        await adapter.SyncSourceContributionAsync(
            [new ShoppingItem(_product1, 5m, _unitId)],
            source: "recipe",
            sourceRef: _recipeId);

        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).ThenInclude(i => i.Contributions).FirstAsync();
            Assert.Single(list.Items);
            Assert.Equal(5m, list.Items[0].Quantity);
        }

        // Third sync — target drops to 1 — slice SET DOWN to 1 (SET semantics, not a no-op)
        await adapter.SyncSourceContributionAsync(
            [new ShoppingItem(_product1, 1m, _unitId)],
            source: "recipe",
            sourceRef: _recipeId);

        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).ThenInclude(i => i.Contributions).FirstAsync();
            Assert.Single(list.Items);
            Assert.Equal(1m, list.Items[0].Quantity); // set down to the latest target
        }
    }

    // ── Multiple items in one batch ────────────────────────────────────────────

    [Fact(DisplayName = "L3 — batch: multiple items in one sync call are each persisted")]
    public async Task Sync_MultipleDifferentProducts_AllPersisted()
    {
        var adapter = BuildAdapter();
        var items = new[]
        {
            new ShoppingItem(_product1, 1m, _unitId),
            new ShoppingItem(_product2, 4m, _unitId),
        };

        await adapter.SyncSourceContributionAsync(items, source: "recipe", sourceRef: _recipeId);

        await using var ctx = NewShoppingDb();
        var list = await ctx.ShoppingLists
            .Include(l => l.Items)
            .ThenInclude(i => i.Contributions)
            .FirstAsync();
        Assert.Equal(2, list.Items.Count);
        // Source/SourceRef now on contributions (plantry-9scq).
        Assert.All(list.Items, i =>
        {
            var contrib = Assert.Single(i.Contributions);
            Assert.Equal(ItemSource.Recipe, contrib.Source);
            Assert.Equal(_recipeId, contrib.SourceRef);
        });
    }

    // ── Empty items is a no-op ────────────────────────────────────────────────

    [Fact(DisplayName = "L3 — empty items: SyncSourceContributionAsync with empty collection leaves list unchanged")]
    public async Task Sync_EmptyCollection_IsNoOp()
    {
        var adapter = BuildAdapter();

        await adapter.SyncSourceContributionAsync([], source: "recipe", sourceRef: _recipeId);

        await using var ctx = NewShoppingDb();
        var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
        Assert.Empty(list.Items);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private IShoppingListWriter BuildAdapter()
    {
        var ctx = NewShoppingDb();
        var repo = new ShoppingListRepository(ctx);
        var tenant = new SimpleTenantContext(_household.Value);
        return new ShoppingListWriterAdapter(repo, NullShoppingCatalogReader.Instance, Clock, tenant);
    }

    private ShoppingDbContext NewShoppingDb()
    {
        var ctx = new ShoppingDbContext(
            new DbContextOptionsBuilder<ShoppingDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class SimpleTenantContext(Guid householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }
}
