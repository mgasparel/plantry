using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests for <see cref="PurchaseJournalReader"/> (P5-10 / DL-O4) — the purchase-frequency
/// read behind the Deals stock-up alerts. Proves, against a real Postgres schema, that it counts only
/// <see cref="StockReason.Purchase"/> movements, respects the trailing-window <c>since</c> boundary, groups
/// per product, and is scoped to the signed-in household by the <c>InventoryDbContext</c> RLS query filter.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PurchaseJournalReaderTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "L3 — counts Purchase movements per product; excludes non-Purchase reasons")]
    public async Task Counts_Purchase_Movements_Per_Product()
    {
        var milk = Guid.CreateVersion7();
        var eggs = Guid.CreateVersion7();
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        await using (var write = NewInventoryDb())
        {
            var milkStock = ProductStock.Start(_household, milk, clock);
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock); // Purchase
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock); // Purchase
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock); // Purchase
            // A Correction addition is NOT a purchase — it must be excluded from the frequency count.
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock, reason: StockReason.Correction);
            await write.ProductStocks.AddAsync(milkStock);

            var eggStock = ProductStock.Start(_household, eggs, clock);
            eggStock.AddStock(1m, _unitId, _locationId, _userId, clock); // Purchase
            await write.ProductStocks.AddAsync(eggStock);

            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new PurchaseJournalReader(read);
        var since = new DateTimeOffset(new DateOnly(2026, 1, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var counts = await reader.CountPurchasesSinceAsync(since);

        Assert.Equal(3, counts[milk]);   // three Purchase rows, the Correction excluded
        Assert.Equal(1, counts[eggs]);
    }

    [Fact(DisplayName = "L3 — respects the trailing-window boundary: purchases before 'since' are excluded")]
    public async Task Respects_Window_Boundary()
    {
        var product = Guid.CreateVersion7();
        var clock = new MutableClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        await using (var write = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, product, clock);
            stock.AddStock(1m, _unitId, _locationId, _userId, clock); // old purchase (Jan)
            clock.Set(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
            stock.AddStock(1m, _unitId, _locationId, _userId, clock); // recent purchase (Jun)
            stock.AddStock(1m, _unitId, _locationId, _userId, clock); // recent purchase (Jun)
            await write.ProductStocks.AddAsync(stock);
            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new PurchaseJournalReader(read);
        // Window opens 2026-03-01 → only the two June purchases count; the January one is before it.
        var since = new DateTimeOffset(new DateOnly(2026, 3, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var counts = await reader.CountPurchasesSinceAsync(since);

        Assert.Equal(2, counts[product]);
    }

    [Fact(DisplayName = "L3 — another household's purchases are invisible (RLS query filter)")]
    public async Task Is_Scoped_To_The_Household()
    {
        var product = Guid.CreateVersion7();
        var otherHousehold = HouseholdId.New();
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        await using (var mine = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, product, clock);
            stock.AddStock(1m, _unitId, _locationId, _userId, clock);
            await mine.ProductStocks.AddAsync(stock);
            await mine.SaveChangesAsync();
        }

        await using (var theirs = NewInventoryDbFor(otherHousehold))
        {
            var stock = ProductStock.Start(otherHousehold, product, clock);
            stock.AddStock(1m, _unitId, _locationId, _userId, clock);
            stock.AddStock(1m, _unitId, _locationId, _userId, clock);
            await theirs.ProductStocks.AddAsync(stock);
            await theirs.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new PurchaseJournalReader(read);
        var since = new DateTimeOffset(new DateOnly(2026, 1, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var counts = await reader.CountPurchasesSinceAsync(since);

        // Only my single purchase — the other household's two are filtered out.
        Assert.Equal(1, counts[product]);
    }

    private DbContextOptions<InventoryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private InventoryDbContext NewInventoryDb() => NewInventoryDbFor(_household);

    private InventoryDbContext NewInventoryDbFor(HouseholdId household)
    {
        var ctx = new InventoryDbContext(InventoryOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private sealed class MutableClock(DateTimeOffset start) : IClock
    {
        private DateTimeOffset _now = start;
        public DateTimeOffset UtcNow => _now;
        public void Set(DateTimeOffset now) => _now = now;
    }
}
