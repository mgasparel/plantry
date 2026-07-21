using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Migrator;
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
            SchemasToInclude = MigrationTargets.All.Select(t => t.Schema).ToArray(),
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

    /// <summary>
    /// Applies every migration-owning DbContext's migrations, in <see cref="MigrationTargets"/>
    /// registry order — the same registry Plantry.Migrator/Program.cs iterates in production, so
    /// this fixture can never drift from what a real deploy creates (plantry-eimm).
    /// </summary>
    private async Task ApplyMigrationsAsync()
    {
        foreach (var target in MigrationTargets.All)
        {
            await using var db = target.CreateContext(ConnectionString);
            await db.Database.MigrateAsync();
        }
    }
}

/// <summary>Collection definition so tests share one container instance.</summary>
[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
