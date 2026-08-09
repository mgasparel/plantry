using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests for <see cref="WasteJournalReader"/> (plantry-h9z9) — the household-wide
/// discard read behind the Today "did you know" stats widget. Proves, against a real Postgres schema,
/// that it counts/finds only <see cref="StockReason.Discarded"/> movements, respects the trailing-window
/// boundary, and is scoped to the signed-in household by the <c>PantryDbContext</c> RLS query filter.
/// Mirrors <c>PurchaseJournalReaderTests</c>'s shape (same journal table, same RLS seam).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class WasteJournalReaderTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly IdentityConverter _converter = new();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "L3 — counts Discarded movements; excludes Consumed and Purchase")]
    public async Task Counts_Discarded_Movements_Only()
    {
        var milk = Guid.CreateVersion7();
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        await using (var write = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, milk, clock);
            stock.AddStock(3m, _unitId, _locationId, _userId, clock); // Purchase — not waste
            stock.Consume(1m, _unitId, StockReason.Consumed, _converter, _userId, clock); // used — not waste
            stock.Consume(1m, _unitId, StockReason.Discarded, _converter, _userId, clock); // wasted
            await write.ProductStocks.AddAsync(stock);
            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new WasteJournalReader(read);
        var since = new DateTimeOffset(new DateOnly(2026, 1, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var count = await reader.CountDiscardedSinceAsync(since);

        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "L3 — respects the trailing-window boundary: discards before 'since' are excluded")]
    public async Task Respects_Window_Boundary()
    {
        var product = Guid.CreateVersion7();
        var clock = new MutableClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        await using (var write = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, product, clock);
            stock.AddStock(3m, _unitId, _locationId, _userId, clock);
            stock.Consume(1m, _unitId, StockReason.Discarded, _converter, _userId, clock); // old discard (Jan)
            clock.Set(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
            stock.Consume(1m, _unitId, StockReason.Discarded, _converter, _userId, clock); // recent discard (Jun)
            await write.ProductStocks.AddAsync(stock);
            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new WasteJournalReader(read);
        // Window opens 2026-03-01 → only the June discard counts; the January one is before it.
        var since = new DateTimeOffset(new DateOnly(2026, 3, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var count = await reader.CountDiscardedSinceAsync(since);

        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "L3 — another household's discards are invisible (RLS query filter)")]
    public async Task Count_Is_Scoped_To_The_Household()
    {
        var product = Guid.CreateVersion7();
        var otherHousehold = HouseholdId.New();
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        await using (var mine = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, product, clock);
            stock.AddStock(1m, _unitId, _locationId, _userId, clock);
            stock.Consume(1m, _unitId, StockReason.Discarded, _converter, _userId, clock);
            await mine.ProductStocks.AddAsync(stock);
            await mine.SaveChangesAsync();
        }

        await using (var theirs = NewInventoryDbFor(otherHousehold))
        {
            var stock = ProductStock.Start(otherHousehold, product, clock);
            stock.AddStock(2m, _unitId, _locationId, _userId, clock);
            stock.Consume(1m, _unitId, StockReason.Discarded, _converter, _userId, clock);
            stock.Consume(1m, _unitId, StockReason.Discarded, _converter, _userId, clock);
            await theirs.ProductStocks.AddAsync(stock);
            await theirs.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new WasteJournalReader(read);
        var since = new DateTimeOffset(new DateOnly(2026, 1, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var count = await reader.CountDiscardedSinceAsync(since);

        // Only my single discard — the other household's two are filtered out.
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "L3 — MostRecentDiscardAsync returns null when nothing has ever been discarded")]
    public async Task MostRecentDiscard_NoDiscards_ReturnsNull()
    {
        var product = Guid.CreateVersion7();
        var clock = new MutableClock(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        await using (var write = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, product, clock);
            stock.AddStock(1m, _unitId, _locationId, _userId, clock);
            stock.Consume(1m, _unitId, StockReason.Consumed, _converter, _userId, clock); // used, never discarded
            await write.ProductStocks.AddAsync(stock);
            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new WasteJournalReader(read);

        Assert.Null(await reader.MostRecentDiscardAsync());
    }

    [Fact(DisplayName = "L3 — MostRecentDiscardAsync returns the latest Discarded timestamp across products")]
    public async Task MostRecentDiscard_ReturnsLatestAcrossProducts()
    {
        var milk = Guid.CreateVersion7();
        var eggs = Guid.CreateVersion7();
        var earlier = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var latest = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(earlier);

        await using (var write = NewInventoryDb())
        {
            var milkStock = ProductStock.Start(_household, milk, clock);
            milkStock.AddStock(1m, _unitId, _locationId, _userId, clock);
            milkStock.Consume(1m, _unitId, StockReason.Discarded, _converter, _userId, clock); // earlier discard
            await write.ProductStocks.AddAsync(milkStock);

            clock.Set(latest);
            var eggStock = ProductStock.Start(_household, eggs, clock);
            eggStock.AddStock(1m, _unitId, _locationId, _userId, clock);
            eggStock.Consume(1m, _unitId, StockReason.Discarded, _converter, _userId, clock); // latest discard
            await write.ProductStocks.AddAsync(eggStock);

            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new WasteJournalReader(read);

        Assert.Equal(latest, await reader.MostRecentDiscardAsync());
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

    /// <summary>Identity converter for consume calls in tests.</summary>
    private sealed class IdentityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}
