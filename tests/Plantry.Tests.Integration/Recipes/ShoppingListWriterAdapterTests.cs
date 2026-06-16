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
/// L3 integration test proving the <see cref="ShoppingListWriterAdapter"/> wiring (P2-4a):
/// calling AddItemsAsync routes through Shopping's real add-item path, stamps source=recipe +
/// source_ref=recipeId, and applies the merge rule (shopping.md resolved call 5) — adding the same
/// missing product twice increments the quantity rather than creating a second row.
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

    [Fact(DisplayName = "L3 — adapter writes items with source=recipe and source_ref=recipeId")]
    public async Task Adapter_Stamps_RecipeSource_And_RecipeId()
    {
        var adapter = BuildAdapter();
        var items = new[] { new ShoppingItem(_product1, 2m, _unitId) };

        await adapter.AddItemsAsync(items, source: "recipe", sourceRef: _recipeId);

        await using var ctx = NewShoppingDb();
        var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
        var item = Assert.Single(list.Items);
        Assert.Equal(_product1, item.ProductId);
        Assert.Equal(2m, item.Quantity);
        Assert.Equal(ItemSource.Recipe, item.Source);
        Assert.Equal(_recipeId, item.SourceRef);
    }

    // ── Merge rule (DM-18 / shopping.md resolved call 5) ──────────────────────

    [Fact(DisplayName = "L3 — merge: adding the same missing product twice increments, not duplicates")]
    public async Task AddItems_SameMissingProduct_Twice_Merges()
    {
        var adapter = BuildAdapter();

        // First call — 2 units of product1
        await adapter.AddItemsAsync(
            [new ShoppingItem(_product1, 2m, _unitId)],
            source: "recipe",
            sourceRef: _recipeId);

        // Verify one row exists
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
            Assert.Single(list.Items);
            Assert.Equal(2m, list.Items[0].Quantity);
        }

        // Second call — 3 more units of product1 — should merge into the same row
        await adapter.AddItemsAsync(
            [new ShoppingItem(_product1, 3m, _unitId)],
            source: "recipe",
            sourceRef: _recipeId);

        // Confirm still one row with quantity = 5 (2+3)
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
            Assert.Single(list.Items);              // NOT two rows
            Assert.Equal(5m, list.Items[0].Quantity); // merged sum
            Assert.Equal(ItemSource.Recipe, list.Items[0].Source);
        }
    }

    // ── Multiple items in one batch ────────────────────────────────────────────

    [Fact(DisplayName = "L3 — batch: multiple items in one AddItems call are each persisted")]
    public async Task AddItems_MultipleDifferentProducts_AllPersisted()
    {
        var adapter = BuildAdapter();
        var items = new[]
        {
            new ShoppingItem(_product1, 1m, _unitId),
            new ShoppingItem(_product2, 4m, _unitId),
        };

        await adapter.AddItemsAsync(items, source: "recipe", sourceRef: _recipeId);

        await using var ctx = NewShoppingDb();
        var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
        Assert.Equal(2, list.Items.Count);
        Assert.All(list.Items, i => Assert.Equal(ItemSource.Recipe, i.Source));
        Assert.All(list.Items, i => Assert.Equal(_recipeId, i.SourceRef));
    }

    // ── Empty items is a no-op ────────────────────────────────────────────────

    [Fact(DisplayName = "L3 — empty items: AddItemsAsync with empty collection leaves list unchanged")]
    public async Task AddItems_EmptyCollection_IsNoOp()
    {
        var adapter = BuildAdapter();

        await adapter.AddItemsAsync([], source: "recipe", sourceRef: _recipeId);

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
