using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Plantry.Migrator;

// One-shot console tool: applies every migration-owning DbContext's migrations (see
// MigrationTargets — the single ordered registry, order is load-bearing) with the database
// owner connection and then reconciles the app_user role password from config.
// Run this before starting Plantry.Web in non-Development environments (ADR-017).
//
// Config keys read (same as Plantry.Web):
//   ConnectionStrings:plantrydb   — owner connection (creates schemas/RLS/roles)
//   Database:AppUserPassword      — the password that app_user should have
//
// Exit codes: 0 = success, 1 = failure (exception logged to stderr).

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var ownerConnStr = config.GetConnectionString("plantrydb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:plantrydb must be configured (owner credentials required for migrations).");

// In production the operator MUST set an explicit password; for local dev the
// well-known fallback matches the docker-compose default.
var appUserPassword = config["Database:AppUserPassword"] ?? "app_user_password";

Console.WriteLine("Plantry.Migrator starting…");
Console.WriteLine("Running migrations with owner credentials.");

try
{
    // Apply every migration-owning DbContext's migrations, in registry order. Order is
    // load-bearing — MigrationTargets.All keeps Identity first because its initial migration
    // creates the app_user role that every other schema's RLS policies depend on.
    foreach (var target in MigrationTargets.All)
    {
        Console.WriteLine($"  Migrating {target.DisplayName} ({target.Schema})…");
        await using var db = target.CreateContext(ownerConnStr);
        await db.Database.MigrateAsync();
    }

    // Open one owner connection for the remaining DDL steps.
    await using var conn = new NpgsqlConnection(ownerConnStr);
    await conn.OpenAsync();

    // Enable pg_stat_statements so the extension view is queryable immediately after
    // a fresh stack up — no manual psql step required by the operator.
    // shared_preload_libraries=pg_stat_statements is set in the postgres container
    // command in both compose stacks; this idempotent CREATE EXTENSION wires up
    // the schema-level view so app_user (and the owner) can query it.
    // See docs/Operations/query-performance.md for the investigation workflow.
    Console.WriteLine("Enabling pg_stat_statements extension…");
    await using (var extCmd = conn.CreateCommand())
    {
        extCmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_stat_statements";
        await extCmd.ExecuteNonQueryAsync();
    }

    // Reconcile app_user password. The initial Identity migration creates the role with a
    // hardcoded literal ('app_user_password'). If the operator configures a different
    // Database:AppUserPassword the role must be updated here so the web runtime can connect.
    Console.WriteLine("Reconciling app_user password…");
    await using var cmd = conn.CreateCommand();
    // Use format string — parameterized DDL is not supported by Postgres.
    // The password value comes from trusted operator config, not user input.
    cmd.CommandText = $"ALTER ROLE app_user WITH PASSWORD '{EscapeSqlLiteral(appUserPassword)}'";
    await cmd.ExecuteNonQueryAsync();

    Console.WriteLine("Plantry.Migrator completed successfully.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Plantry.Migrator FAILED: {ex}");
    return 1;
}

/// <summary>
/// Escapes a single-quoted Postgres string literal by doubling any embedded single-quotes.
/// </summary>
static string EscapeSqlLiteral(string value) => value.Replace("'", "''");
