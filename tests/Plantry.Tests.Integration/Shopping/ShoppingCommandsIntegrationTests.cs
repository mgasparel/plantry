using Microsoft.EntityFrameworkCore;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;
using Plantry.Shopping.Infrastructure;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Shopping;

/// <summary>
/// L3 integration tests for AddItemCommand, CheckOffCommand, and ClearCheckedCommand
/// against the real ShoppingDbContext + Postgres schema (shopping.md resolved calls 4/5,
/// SPEC §3b/§3c/§3e, DM-18).
///
/// Focus: merge rule against real persistence — proves that after SaveAsync the aggregate
/// is reloaded from the DB with the merged quantity (not two separate rows).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ShoppingCommandsIntegrationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly Guid _product1 = Guid.CreateVersion7();
    private readonly Guid _product2 = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed the household's shopping list via the production seeder.
        await using var ctx = NewShoppingDb();
        var seeder = new ShoppingReferenceDataSeeder(ctx, SystemClock.Instance);
        await seeder.SeedAsync(_household);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Merge rule against real DB ────────────────────────────────────────────

    [Fact(DisplayName = "L3 — merge: adding the same product twice merges into one DB row with summed quantity")]
    public async Task AddItem_SameProduct_MergesIntoOneDbRow()
    {
        var tenant = new TenantContext();
        tenant.Set(_household.Value);

        // First add — 2 units
        await using (var ctx = NewShoppingDb())
        {
            var repo = new ShoppingListRepository(ctx);
            var cmd = new AddItemCommand(
                _product1, null, 2m, _unitId, null,
                ItemSource.Manual, null, false,
                repo, NullShoppingCatalogReader.Instance, SystemClock.Instance, new SimpleTenantContext(_household.Value));
            var r = await cmd.ExecuteAsync();
            Assert.True(r.IsSuccess);
        }

        // Reload and assert one row with qty = 2
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
            Assert.Single(list.Items);
            Assert.Equal(2m, list.Items[0].Quantity);
        }

        // Second add — 3 units — should merge
        await using (var ctx = NewShoppingDb())
        {
            var repo = new ShoppingListRepository(ctx);
            var cmd = new AddItemCommand(
                _product1, null, 3m, _unitId, null,
                ItemSource.Manual, null, false,
                repo, NullShoppingCatalogReader.Instance, SystemClock.Instance, new SimpleTenantContext(_household.Value));
            var r = await cmd.ExecuteAsync();
            Assert.True(r.IsSuccess);
        }

        // Reload and confirm still one row with qty = 5
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
            Assert.Single(list.Items);               // NOT two rows
            Assert.Equal(5m, list.Items[0].Quantity); // merged quantity
        }
    }

    [Fact(DisplayName = "L3 — intentional-dup: flagged second add inserts two DB rows for the same product")]
    public async Task AddItem_IntentionalDuplicate_InsertsTwoDbRows()
    {
        // Add product1 the first time
        await using (var ctx = NewShoppingDb())
        {
            var repo = new ShoppingListRepository(ctx);
            var cmd = new AddItemCommand(
                _product1, null, 1m, _unitId, null,
                ItemSource.Manual, null, false,
                repo, NullShoppingCatalogReader.Instance, SystemClock.Instance, new SimpleTenantContext(_household.Value));
            Assert.True((await cmd.ExecuteAsync()).IsSuccess);
        }

        // Add product1 again with intentionalDuplicate=true
        await using (var ctx = NewShoppingDb())
        {
            var repo = new ShoppingListRepository(ctx);
            var cmd = new AddItemCommand(
                _product1, null, 1m, _unitId, null,
                ItemSource.Manual, null, intentionalDuplicate: true,
                repo, NullShoppingCatalogReader.Instance, SystemClock.Instance, new SimpleTenantContext(_household.Value));
            Assert.True((await cmd.ExecuteAsync()).IsSuccess);
        }

        // Reload and confirm two rows
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
            Assert.Equal(2, list.Items.Count);
        }
    }

    [Fact(DisplayName = "L3 — check-off then clear: checked items are hard-deleted; unchecked items survive")]
    public async Task CheckOff_ThenClear_HardDeletesOnlyCheckedItems()
    {
        ShoppingListId listId;
        ShoppingListItemId item1Id;

        // Seed product1 and product2
        await using (var ctx = NewShoppingDb())
        {
            var repo = new ShoppingListRepository(ctx);
            var tenant = new SimpleTenantContext(_household.Value);

            var r1 = await new AddItemCommand(_product1, null, 1m, _unitId, null,
                ItemSource.Manual, null, false, repo, NullShoppingCatalogReader.Instance, SystemClock.Instance, tenant).ExecuteAsync();
            Assert.True(r1.IsSuccess);
            item1Id = r1.Value;

            await new AddItemCommand(_product2, null, 1m, _unitId, null,
                ItemSource.Manual, null, false, repo, NullShoppingCatalogReader.Instance, SystemClock.Instance, tenant).ExecuteAsync();

            var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
            listId = list.Id;
        }

        // Check off product1
        await using (var ctx = NewShoppingDb())
        {
            var repo = new ShoppingListRepository(ctx);
            var tenant = new SimpleTenantContext(_household.Value);
            var result = await new CheckOffCommand(listId, item1Id, _userId, repo, SystemClock.Instance, tenant)
                .ExecuteAsync();
            Assert.True(result.IsSuccess);
        }

        // Verify check-off persisted
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
            var item1 = list.Items.Single(i => i.Id == item1Id);
            Assert.True(item1.IsChecked);
        }

        // Clear checked
        await using (var ctx = NewShoppingDb())
        {
            var repo = new ShoppingListRepository(ctx);
            var tenant = new SimpleTenantContext(_household.Value);
            var clearResult = await new ClearCheckedCommand(repo, SystemClock.Instance, tenant).ExecuteAsync();
            Assert.True(clearResult.IsSuccess);
            Assert.Equal(1, clearResult.Value);
        }

        // Verify only product2 remains
        await using (var ctx = NewShoppingDb())
        {
            var list = await ctx.ShoppingLists.Include(l => l.Items).FirstAsync();
            Assert.Single(list.Items);
            Assert.Equal(_product2, list.Items[0].ProductId);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private DbContextOptions<ShoppingDbContext> ShoppingOptions() =>
        new DbContextOptionsBuilder<ShoppingDbContext>().UseNpgsql(db.ConnectionString).Options;

    private ShoppingDbContext NewShoppingDb()
    {
        var ctx = new ShoppingDbContext(ShoppingOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    /// <summary>
    /// Minimal ITenantContext for integration tests — avoids importing a Fake from the unit test project.
    /// </summary>
    private sealed class SimpleTenantContext(Guid householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }
}

/// <summary>
/// Stub <see cref="IShoppingCatalogReader"/> for integration tests — conversion is not exercised
/// by the persistence-focused L3 tests; TryConvertAsync always returns null so the fallback
/// (insert a second line) would apply for any unit-mismatch scenario, but no such scenario is
/// tested here.
/// </summary>
internal sealed class NullShoppingCatalogReader : IShoppingCatalogReader
{
    public static readonly NullShoppingCatalogReader Instance = new();
    public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, ShoppingProductSummary>>(new Dictionary<Guid, ShoppingProductSummary>());
    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(IReadOnlyList<Guid> unitIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShoppingProductCandidate>>([]);
    public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default)
        => Task.FromResult<decimal?>(null);
    public Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShoppingUnitOption>>([]);
    public Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShoppingCategoryOption>>([]);
}
