using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Xunit;

namespace Plantry.Tests.Integration.Infrastructure;

/// <summary>
/// Shared Testcontainers fixture: boots a bare Postgres container once per test collection,
/// applies all migrations, and provides Respawn for fast inter-test database reset.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("plantry_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    private Respawner? _respawner;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// Connection string for the non-superuser 'app_user' role created by the migrations.
    /// RLS never applies to superusers (the Testcontainers bootstrap user is one), so
    /// RLS-backstop tests must connect as this role for the policies to take effect.
    /// </summary>
    public string AppUserConnectionString =>
        new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = "app_user",
            Password = "app_user_password",
        }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyMigrationsAsync();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["identity", "catalog", "inventory", "pricing", "intake"],
        });
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Resets all data to a clean state between tests.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await _respawner!.ResetAsync(conn);
    }

    private async Task ApplyMigrationsAsync()
    {
        var identityOpts = new DbContextOptionsBuilder<Plantry.Identity.Infrastructure.PlantryIdentityDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var identityDb = new Plantry.Identity.Infrastructure.PlantryIdentityDbContext(identityOpts);
        await identityDb.Database.MigrateAsync();

        var catalogOpts = new DbContextOptionsBuilder<Plantry.Catalog.Infrastructure.CatalogDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var catalogDb = new Plantry.Catalog.Infrastructure.CatalogDbContext(catalogOpts);
        await catalogDb.Database.MigrateAsync();

        var inventoryOpts = new DbContextOptionsBuilder<Plantry.Inventory.Infrastructure.InventoryDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var inventoryDb = new Plantry.Inventory.Infrastructure.InventoryDbContext(inventoryOpts);
        await inventoryDb.Database.MigrateAsync();

        var pricingOpts = new DbContextOptionsBuilder<Plantry.Pricing.Infrastructure.PricingDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var pricingDb = new Plantry.Pricing.Infrastructure.PricingDbContext(pricingOpts);
        await pricingDb.Database.MigrateAsync();

        var intakeOpts = new DbContextOptionsBuilder<Plantry.Intake.Infrastructure.IntakeDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var intakeDb = new Plantry.Intake.Infrastructure.IntakeDbContext(intakeOpts);
        await intakeDb.Database.MigrateAsync();
    }
}

/// <summary>Collection definition so tests share one container instance.</summary>
[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
