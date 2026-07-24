using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests for <see cref="JournalEntriesBySourceRefReader"/> (plantry-0eut) — the batched
/// journal-by-SourceRef read behind the MealPlanning cook-status port's product-dish leg. Proves, against
/// a real Postgres schema, that it groups movements by SourceRef, batches multiple refs in one query, and
/// is scoped to the signed-in household by the <c>InventoryDbContext</c> RLS query filter.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class JournalEntriesBySourceRefReaderTests(PostgresFixture db) : IAsyncLifetime
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

    [Fact(DisplayName = "L3 — groups journal movements by SourceRef, batching multiple refs in one query")]
    public async Task Groups_Movements_By_SourceRef()
    {
        var product = Guid.CreateVersion7();
        var dishA = Guid.NewGuid();
        var dishB = Guid.NewGuid();
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 24, 18, 42, 0, TimeSpan.Zero));

        await using (var write = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, product, clock);
            stock.AddStock(10m, _unitId, _locationId, _userId, clock);
            var converter = new IdentityConverter();
            stock.Consume(4m, _unitId, StockReason.Consumed, converter, _userId, clock, sourceRef: dishA);
            clock.Set(clock.UtcNow.AddMinutes(5));
            stock.Consume(2m, _unitId, StockReason.Consumed, converter, _userId, clock, sourceRef: dishB);
            await write.ProductStocks.AddAsync(stock);
            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new JournalEntriesBySourceRefReader(read);

        var byRef = await reader.ListBySourceRefsAsync([dishA, dishB, Guid.NewGuid()]);

        Assert.Equal(2, byRef.Count);
        Assert.Equal(-4m, Assert.Single(byRef[dishA]).Delta);
        Assert.Equal(-2m, Assert.Single(byRef[dishB]).Delta);
    }

    [Fact(DisplayName = "L3 — a SourceRef with an eat then a compensating undo ADD returns both movements")]
    public async Task Returns_All_Movements_For_A_Ref_Eat_And_Undo()
    {
        var product = Guid.CreateVersion7();
        var dish = Guid.NewGuid();
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero));

        await using (var write = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, product, clock);
            stock.AddStock(10m, _unitId, _locationId, _userId, clock);
            var converter = new IdentityConverter();
            stock.Consume(4m, _unitId, StockReason.Consumed, converter, _userId, clock, sourceRef: dish);
            clock.Set(clock.UtcNow.AddMinutes(10));
            // Compensating undo: a +4 ADD against the same SourceRef.
            stock.AddStock(4m, _unitId, _locationId, _userId, clock, sourceRef: dish, reason: StockReason.Correction);
            await write.ProductStocks.AddAsync(stock);
            await write.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new JournalEntriesBySourceRefReader(read);

        var byRef = await reader.ListBySourceRefsAsync([dish]);

        var movements = byRef[dish];
        Assert.Equal(2, movements.Count);
        Assert.Equal(0m, movements.Sum(m => m.Delta)); // nets to zero — fully undone
    }

    [Fact(DisplayName = "L3 — a SourceRef with no journal rows is absent from the result")]
    public async Task Absent_SourceRef_Is_Not_In_The_Result()
    {
        await using var read = NewInventoryDb();
        var reader = new JournalEntriesBySourceRefReader(read);

        var byRef = await reader.ListBySourceRefsAsync([Guid.NewGuid()]);

        Assert.Empty(byRef);
    }

    [Fact(DisplayName = "L3 — another household's journal rows are invisible (RLS query filter)")]
    public async Task Is_Scoped_To_The_Household()
    {
        var product = Guid.CreateVersion7();
        var dish = Guid.NewGuid();
        var otherHousehold = HouseholdId.New();
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero));

        await using (var theirs = NewInventoryDbFor(otherHousehold))
        {
            var stock = ProductStock.Start(otherHousehold, product, clock);
            stock.AddStock(10m, _unitId, _locationId, _userId, clock);
            stock.Consume(4m, _unitId, StockReason.Consumed, new IdentityConverter(), _userId, clock, sourceRef: dish);
            await theirs.ProductStocks.AddAsync(stock);
            await theirs.SaveChangesAsync();
        }

        await using var read = NewInventoryDb();
        var reader = new JournalEntriesBySourceRefReader(read);

        var byRef = await reader.ListBySourceRefsAsync([dish]);

        Assert.Empty(byRef);
    }

    [Fact(DisplayName = "L3 — empty input returns empty without querying")]
    public async Task Empty_Input_Returns_Empty()
    {
        await using var read = NewInventoryDb();
        var reader = new JournalEntriesBySourceRefReader(read);

        Assert.Empty(await reader.ListBySourceRefsAsync([]));
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

    /// <summary>Single-unit identity converter — these scenarios never cross units.</summary>
    private sealed class IdentityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}
