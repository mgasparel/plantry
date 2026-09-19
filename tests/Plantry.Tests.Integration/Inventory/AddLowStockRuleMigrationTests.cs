using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Plantry.Pantry.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace Plantry.Tests.Integration.Inventory;

/// <summary>
/// Migration-behavior harness (plantry-oh27.1, mirroring
/// <see cref="Plantry.Tests.Integration.Housekeeping.BackfillYieldProductsIsProducedMigrationTests"/>)
/// for <c>Migrations/Pantry/20260919184735_AddLowStockRule.cs</c> — proves the data move copies every
/// non-null positive legacy <c>product_stock.low_stock_threshold</c> into the new
/// <c>inventory.low_stock_rule</c> table (and only those), and that the old column is gone afterwards.
///
/// Boots its own disposable Postgres container (the shared <see cref="Infrastructure.PostgresFixture"/>
/// migrates PantryDbContext straight to latest, leaving no seam to seed pre-migration data), migrates
/// PantryDbContext only as far as the migration immediately preceding the one under test, seeds
/// pre-migration <c>product_stock</c> rows via raw SQL (the current EF model no longer has
/// <c>LowStockThreshold</c> to seed through), then migrates forward through <c>AddLowStockRule</c> and
/// asserts on the result.
/// </summary>
public sealed class AddLowStockRuleMigrationTests : IAsyncLifetime
{
    private const string BaselineMigration = "20260813053740_AddDefaultProducedCategory";
    private const string MigrationUnderTest = "20260919184735_AddLowStockRule";
    private const string MigrationsAssembly = "Plantry.Pantry.Infrastructure";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("plantry_migration_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact(DisplayName =
        "A positive legacy threshold moves to low_stock_rule; null and zero legacy thresholds are left behind (no rule created)")]
    public async Task PositiveThresholds_Move_NullAndZero_DoNot()
    {
        await MigrateToAsync(BaselineMigration);

        var household = Guid.NewGuid();
        var withThreshold = Guid.NewGuid();
        var withNullThreshold = Guid.NewGuid();
        var withZeroThreshold = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var conn = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO inventory.product_stock (household_id, product_id, created_at, updated_at, low_stock_threshold)
                VALUES
                    (@household, @withThreshold, @now, @now, 3.5),
                    (@household, @withNull, @now, @now, NULL),
                    (@household, @withZero, @now, @now, 0);
                """;
            cmd.Parameters.AddWithValue("household", household);
            cmd.Parameters.AddWithValue("withThreshold", withThreshold);
            cmd.Parameters.AddWithValue("withNull", withNullThreshold);
            cmd.Parameters.AddWithValue("withZero", withZeroThreshold);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        await MigrateToAsync(MigrationUnderTest);

        await using var verify = new NpgsqlConnection(_container.GetConnectionString());
        await verify.OpenAsync();

        // The old column is gone.
        await using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = """
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'inventory' AND table_name = 'product_stock' AND column_name = 'low_stock_threshold';
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }

        // Only the positive-threshold product got a rule row, carrying the exact threshold.
        await using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = "SELECT product_id, threshold FROM inventory.low_stock_rule WHERE household_id = @household;";
            cmd.Parameters.AddWithValue("household", household);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(withThreshold, reader.GetGuid(0));
            Assert.Equal(3.5m, reader.GetDecimal(1));
            Assert.False(await reader.ReadAsync()); // exactly one row — null/zero never moved
        }
    }

    [Fact(DisplayName = "Down migration reverses: drops low_stock_rule, restores the column, and copies data back")]
    public async Task Down_Reverses_Restores_Column_And_Data()
    {
        await MigrateToAsync(BaselineMigration);

        var household = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var conn = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO inventory.product_stock (household_id, product_id, created_at, updated_at, low_stock_threshold)
                VALUES (@household, @productId, @now, @now, 7.25);
                """;
            cmd.Parameters.AddWithValue("household", household);
            cmd.Parameters.AddWithValue("productId", productId);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        await MigrateToAsync(MigrationUnderTest);
        await MigrateToAsync(BaselineMigration); // down

        await using var verify = new NpgsqlConnection(_container.GetConnectionString());
        await verify.OpenAsync();

        await using var cmd2 = verify.CreateCommand();
        cmd2.CommandText = "SELECT low_stock_threshold FROM inventory.product_stock WHERE product_id = @productId;";
        cmd2.Parameters.AddWithValue("productId", productId);
        var result = await cmd2.ExecuteScalarAsync();

        Assert.Equal(7.25m, result);
    }

    private async Task MigrateToAsync(string targetMigration)
    {
        await using var ctx = NewPantryContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private PantryDbContext NewPantryContext()
    {
        var opts = new DbContextOptionsBuilder<PantryDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly(MigrationsAssembly))
            .Options;
        return new PantryDbContext(opts);
    }
}
