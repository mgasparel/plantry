using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests for <see cref="PurchaseJournalReader"/> (P5-10 / DL-O4) — the purchase-frequency
/// read behind the Deals stock-up alerts. Proves, against a real Postgres schema, that it counts only
/// <see cref="StockReason.Purchase"/> movements, respects the trailing-window <c>since</c> boundary, groups
/// per product, and is scoped to the signed-in household by the <c>PantryDbContext</c> RLS query filter.
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

    // ── plantry-gtgl: PurchaseDatesForProductsAsync (Deals-review purchase-cadence estimate) ─────────

    [Fact(DisplayName = "L3 — returns purchase timestamps per product, oldest-first; excludes non-Purchase reasons")]
    public async Task Returns_Purchase_Timestamps_Per_Product_Oldest_First()
    {
        var milk = Guid.CreateVersion7();
        var eggs = Guid.CreateVersion7();
        var t1 = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(t1);

        await using (var write = NewInventoryDb())
        {
            var milkStock = ProductStock.Start(_household, milk, clock);
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock); // Purchase @ t1
            clock.Set(t3);
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock); // Purchase @ t3 — seeded out of order on purpose
            clock.Set(t2);
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock); // Purchase @ t2
            // A Correction addition is NOT a purchase — it must be excluded from the dates read.
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock, reason: StockReason.Correction);
            await write.ProductStocks.AddAsync(milkStock);

            var eggStock = ProductStock.Start(_household, eggs, clock);
            eggStock.AddStock(1m, _unitId, _locationId, _userId, clock); // Purchase @ t2
            await write.ProductStocks.AddAsync(eggStock);

            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new PurchaseJournalReader(read);

        var dates = await reader.PurchaseDatesForProductsAsync([milk, eggs]);

        Assert.Equal(2, dates.Count);
        Assert.Equal(new[] { t1, t2, t3 }, dates[milk]); // ascending despite insertion order; the Correction excluded
        Assert.Equal(new[] { t2 }, dates[eggs]);
    }

    [Fact(DisplayName = "L3 — another household's purchase dates are invisible (RLS query filter)")]
    public async Task PurchaseDates_Are_Scoped_To_The_Household()
    {
        var product = Guid.CreateVersion7();
        var otherHousehold = HouseholdId.New();
        var mineAt = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var theirsAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var mine = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, product, new MutableClock(mineAt));
            stock.AddStock(1m, _unitId, _locationId, _userId, new MutableClock(mineAt));
            await mine.ProductStocks.AddAsync(stock);
            await mine.SaveChangesAsync();
        }

        await using (var theirs = NewInventoryDbFor(otherHousehold))
        {
            var clock = new MutableClock(theirsAt);
            var stock = ProductStock.Start(otherHousehold, product, clock);
            stock.AddStock(1m, _unitId, _locationId, _userId, clock);
            stock.AddStock(1m, _unitId, _locationId, _userId, clock);
            await theirs.ProductStocks.AddAsync(stock);
            await theirs.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new PurchaseJournalReader(read);

        var dates = await reader.PurchaseDatesForProductsAsync([product]);

        // Only my single purchase timestamp — the other household's two are filtered out.
        var only = Assert.Single(dates[product]);
        Assert.Equal(mineAt, only);
    }

    [Fact(DisplayName = "L3 — empty input short-circuits to an empty result with no query")]
    public async Task PurchaseDates_Empty_Input_Returns_Empty()
    {
        await using var read = NewInventoryDb();
        var reader = new PurchaseJournalReader(read);
        Assert.Empty(await reader.PurchaseDatesForProductsAsync([]));
    }

    private DbContextOptions<PantryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<PantryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private PantryDbContext NewInventoryDb() => NewInventoryDbFor(_household);

    private PantryDbContext NewInventoryDbFor(HouseholdId household)
    {
        var ctx = new PantryDbContext(InventoryOptions());
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
