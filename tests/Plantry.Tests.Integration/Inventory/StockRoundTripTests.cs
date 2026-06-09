using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// L3 integration tests proving the <see cref="ProductStock"/> aggregate — its composite
/// <c>(household_id, product_id)</c> key, its <see cref="StockEntry"/> lots, and the append-only
/// <see cref="StockJournalEntry"/> rows — round-trips through EF against a real Postgres schema, and
/// that the within-context FKs (lot → root, journal → live lot) are physically enforced
/// (PHASE-1-PLAN.md Slice 2, Stage B; inventory.md; DM-14).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StockRoundTripTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "ProductStock round-trips with its lots and journal rows through EF")]
    public async Task ProductStock_RoundTrips_With_Lots_And_Journal()
    {
        await using (var db1 = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
            stock.AddStock(500m, _unitId, _locationId, _userId, SystemClock.Instance,
                expiryDate: new DateOnly(2026, 7, 1), purchasedAt: new DateOnly(2026, 6, 1));
            stock.AddStock(250m, _unitId, _locationId, _userId, SystemClock.Instance,
                expiryDate: new DateOnly(2026, 6, 20));
            await db1.ProductStocks.AddAsync(stock);
            await db1.SaveChangesAsync();
        }

        await using var db2 = NewInventoryDb();
        var loaded = await db2.ProductStocks
            .Include(p => p.Entries)
            .Include(p => p.Journal)
            .SingleAsync(p => p.HouseholdId == _household && p.ProductId == _productId);

        Assert.Equal(_household, loaded.HouseholdId);
        Assert.Equal(_productId, loaded.ProductId);
        Assert.Equal(2, loaded.Entries.Count);
        Assert.All(loaded.Entries, e => Assert.Equal(_household, e.HouseholdId));
        Assert.All(loaded.Entries, e => Assert.True(e.IsActive));

        // Both intakes wrote a positive Purchase journal row.
        Assert.Equal(2, loaded.Journal.Count);
        Assert.All(loaded.Journal, j => Assert.Equal(StockReason.Purchase, j.Reason));
        Assert.All(loaded.Journal, j => Assert.True(j.Delta > 0m));
        Assert.Equal(750m, loaded.Journal.Sum(j => j.Delta));
    }

    [Fact(DisplayName = "FEFO ordering survives the EF round-trip (soonest expiry first)")]
    public async Task ActiveLotsFefo_Returns_SoonestExpiry_First_AfterReload()
    {
        StockEntryId soonId, midId, noExpiryId;

        await using (var db1 = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
            var noExpiry = stock.AddStock(10m, _unitId, _locationId, _userId, SystemClock.Instance);
            var mid = stock.AddStock(10m, _unitId, _locationId, _userId, SystemClock.Instance,
                expiryDate: new DateOnly(2026, 8, 1));
            var soon = stock.AddStock(10m, _unitId, _locationId, _userId, SystemClock.Instance,
                expiryDate: new DateOnly(2026, 7, 1));
            await db1.ProductStocks.AddAsync(stock);
            await db1.SaveChangesAsync();
            soonId = soon.Id;
            midId = mid.Id;
            noExpiryId = noExpiry.Id;
        }

        await using var db2 = NewInventoryDb();
        var loaded = await db2.ProductStocks
            .Include(p => p.Entries)
            .SingleAsync(p => p.HouseholdId == _household && p.ProductId == _productId);

        var fefo = loaded.ActiveLotsFefo().Select(e => e.Id).ToList();
        Assert.Equal([soonId, midId, noExpiryId], fefo); // nulls (no expiry) consumed last
    }

    [Fact(DisplayName = "Composite FK rejects a lot whose (household_id, product_id) has no root")]
    public async Task CompositeForeignKey_Rejects_Orphan_Lot()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.stock_entry
                (entry_id, household_id, product_id, quantity, unit_id, location_id,
                 is_open, created_at, updated_at)
            VALUES (@id, @hid, @pid, 1, @unit, @loc, false, now(), now())
            """;
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("hid", _household.Value);   // no product_stock root exists
        cmd.Parameters.AddWithValue("pid", _productId);
        cmd.Parameters.AddWithValue("unit", _unitId);
        cmd.Parameters.AddWithValue("loc", _locationId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact(DisplayName = "Journal FK rejects a row pointing at a non-existent lot (DM-14)")]
    public async Task JournalForeignKey_Rejects_Orphan_EntryId()
    {
        // A real root + lot exists, isolating the entry_id FK: the journal row carries a valid
        // (household_id, product_id) but a bogus entry_id.
        await using (var db1 = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
            stock.AddStock(5m, _unitId, _locationId, _userId, SystemClock.Instance);
            await db1.ProductStocks.AddAsync(stock);
            await db1.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.stock_journal_entry
                (journal_id, household_id, product_id, entry_id, delta, unit_id,
                 reason, occurred_at, user_id)
            VALUES (@id, @hid, @pid, @entry, -1, @unit, 'Consumed', now(), @user)
            """;
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", _productId);
        cmd.Parameters.AddWithValue("entry", Guid.CreateVersion7()); // no such stock_entry
        cmd.Parameters.AddWithValue("unit", _unitId);
        cmd.Parameters.AddWithValue("user", _userId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact(DisplayName = "Reason CHECK constraint rejects an unknown journal reason")]
    public async Task ReasonCheckConstraint_Rejects_Unknown_Reason()
    {
        await using (var db1 = NewInventoryDb())
        {
            var stock = ProductStock.Start(_household, _productId, SystemClock.Instance);
            stock.AddStock(5m, _unitId, _locationId, _userId, SystemClock.Instance);
            await db1.ProductStocks.AddAsync(stock);
            await db1.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        Guid entryId;
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT entry_id FROM inventory.stock_entry WHERE product_id = @pid LIMIT 1";
            read.Parameters.AddWithValue("pid", _productId);
            entryId = (Guid)(await read.ExecuteScalarAsync())!;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.stock_journal_entry
                (journal_id, household_id, product_id, entry_id, delta, unit_id,
                 reason, occurred_at, user_id)
            VALUES (@id, @hid, @pid, @entry, -1, @unit, 'Teleported', now(), @user)
            """;
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", _productId);
        cmd.Parameters.AddWithValue("entry", entryId);
        cmd.Parameters.AddWithValue("unit", _unitId);
        cmd.Parameters.AddWithValue("user", _userId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    private DbContextOptions<InventoryDbContext> InventoryOptions() =>
        new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;

    private InventoryDbContext NewInventoryDb()
    {
        var ctx = new InventoryDbContext(InventoryOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
