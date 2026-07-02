using Microsoft.EntityFrameworkCore;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;
using Plantry.Shopping.Infrastructure;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Tests.Integration.Shopping;
using Plantry.Web.Deals;
using Xunit;

namespace Plantry.Tests.Integration.Deals;

/// <summary>
/// L3 integration test proving the <see cref="DealShoppingListWriterAdapter"/> wiring (P5-10 / DJ5): the
/// stock-up "Add to list" action routes through Shopping's real add-item path, stamps <c>source=deal</c> +
/// <c>source_ref=dealId</c>, and reuses the P2-4 seam's merge rule (DM-18) so re-adding the same deal for
/// the same product tops up the existing contribution rather than inserting a duplicate row.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DealShoppingListWriterAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private readonly Guid _product = Guid.CreateVersion7();
    private readonly DealId _dealId = DealId.New();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var ctx = NewShoppingDb();
        var seeder = new ShoppingReferenceDataSeeder(ctx, Clock);
        await seeder.SeedAsync(_household);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "L3 — 'Add to list' stamps source=deal + source_ref=dealId over the P2-4 seam")]
    public async Task AddItem_Stamps_DealSource_And_DealId()
    {
        var adapter = BuildAdapter();

        await adapter.AddItemAsync(_product, _dealId);

        await using var ctx = NewShoppingDb();
        var list = await ctx.ShoppingLists
            .Include(l => l.Items)
            .ThenInclude(i => i.Contributions)
            .FirstAsync();

        var item = Assert.Single(list.Items);
        Assert.Equal(_product, item.ProductId);
        var contrib = Assert.Single(item.Contributions);
        Assert.Equal(ItemSource.Deal, contrib.Source);
        Assert.Equal(_dealId.Value, contrib.SourceRef);
    }

    [Fact(DisplayName = "L3 — merge/no-dup: re-adding the same deal for the same product leaves ONE row (no duplicate)")]
    public async Task AddItem_SameDealTwice_Merges_NoDuplicate()
    {
        var adapter = BuildAdapter();

        await adapter.AddItemAsync(_product, _dealId);
        await adapter.AddItemAsync(_product, _dealId);

        await using var ctx = NewShoppingDb();
        var list = await ctx.ShoppingLists
            .Include(l => l.Items)
            .ThenInclude(i => i.Contributions)
            .FirstAsync();

        // One item row, one Deal contribution — the second add merged into the first (DM-18).
        var item = Assert.Single(list.Items);
        var contrib = Assert.Single(item.Contributions);
        Assert.Equal(ItemSource.Deal, contrib.Source);
        Assert.Equal(_dealId.Value, contrib.SourceRef);
    }

    private DealShoppingListWriterAdapter BuildAdapter()
    {
        var ctx = NewShoppingDb();
        var repo = new ShoppingListRepository(ctx);
        var tenant = new SimpleTenantContext(_household.Value);
        return new DealShoppingListWriterAdapter(repo, NullShoppingCatalogReader.Instance, Clock, tenant);
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
