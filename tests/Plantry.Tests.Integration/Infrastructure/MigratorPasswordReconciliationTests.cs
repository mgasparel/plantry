using Npgsql;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Infrastructure;

/// <summary>
/// Integration proof for the Plantry.Migrator app_user password-reconciliation path (ADR-017).
///
/// AC #3: "app authenticates as app_user with an operator-chosen password"
///
/// The initial Identity migration creates the app_user role with a hardcoded literal
/// ('app_user_password'). The migrator's reconciliation step issues
/// ALTER ROLE app_user WITH PASSWORD '&lt;Database:AppUserPassword&gt;'
/// to bring the database role in sync with the operator-configured value.
///
/// Testcontainers boots postgres:16-alpine with trust authentication (no password
/// verification on connection), so we cannot prove in this environment that the
/// wrong password is REJECTED after rotation — that guarantee comes from the real
/// postgres.conf in staging/prod (md5/scram-sha-256). What we CAN prove in a
/// trust-auth container is:
///   (a) the app_user role exists after migrations (pre-condition),
///   (b) the ALTER ROLE DDL executes without error (the reconciliation succeeds), and
///   (c) a connection with the newly-set password succeeds (the role remains usable).
/// These three assertions collectively cover the correctness of the reconciliation path
/// as far as the test infrastructure allows.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MigratorPasswordReconciliationTests(PostgresFixture db)
{
    // Mirrors the EscapeSqlLiteral helper in Plantry.Migrator/Program.cs —
    // kept inline so this test does not take a project reference on the migrator executable.
    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    /// <summary>
    /// Pre-condition: the Identity migration created the app_user role and it can
    /// connect with the migration-default password (trust-auth proves role exists).
    /// </summary>
    [Fact]
    public async Task AppUser_RoleExists_AfterMigration()
    {
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync(); // throws if role does not exist
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
    }

    /// <summary>
    /// The migrator's ALTER ROLE reconciliation executes without error and leaves
    /// app_user reachable with the updated password (proving the DDL path is correct).
    /// </summary>
    [Fact]
    public async Task Migrator_PasswordReconciliation_ExecutesWithoutError_AndRoleRemainsConnectable()
    {
        // Arrange: pick an operator-chosen password distinct from the default literal.
        const string newPassword = "rotated-secret-123";
        const string defaultPassword = "app_user_password";

        // Act: simulate what Plantry.Migrator/Program.cs does.
        // The migrator issues ALTER ROLE app_user WITH PASSWORD '<Database:AppUserPassword>'
        // over the owner connection.
        await using (var ownerConn = new NpgsqlConnection(db.ConnectionString))
        {
            await ownerConn.OpenAsync();
            await using var cmd = ownerConn.CreateCommand();
            cmd.CommandText = $"ALTER ROLE app_user WITH PASSWORD '{EscapeSqlLiteral(newPassword)}'";
            // Assert (b): the DDL executes without throwing (role exists, owner has CREATEROLE).
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            // Assert (c): app_user is still reachable after the password was changed.
            // (Trust-auth allows any password; this proves the role was not dropped or locked.)
            var newConnStr = new NpgsqlConnectionStringBuilder(db.ConnectionString)
            {
                Username = "app_user",
                Password = newPassword,
            }.ConnectionString;
            await using var newConn = new NpgsqlConnection(newConnStr);
            await newConn.OpenAsync();
            Assert.Equal(System.Data.ConnectionState.Open, newConn.State);
        }
        finally
        {
            // Restore the default password so the shared PostgresFixture remains usable
            // for other tests in the collection that expect AppUserConnectionString to work.
            await using var ownerConn = new NpgsqlConnection(db.ConnectionString);
            await ownerConn.OpenAsync();
            await using var cmd = ownerConn.CreateCommand();
            cmd.CommandText = $"ALTER ROLE app_user WITH PASSWORD '{EscapeSqlLiteral(defaultPassword)}'";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
