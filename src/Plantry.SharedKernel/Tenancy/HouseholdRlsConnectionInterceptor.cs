using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Plantry.SharedKernel.Tenancy;

/// <summary>
/// Arms the Postgres row-level-security backstop on the live connection by setting the
/// <c>app.household_id</c> session GUC every time a connection is opened, from the ambient
/// <see cref="ITenantContext"/>. The migrations' RLS policies key off
/// <c>current_setting('app.household_id')</c>; without this they are inert at runtime and
/// tenant isolation collapses onto the EF query filter alone.
///
/// The GUC is re-applied on every open (not once per request) because Npgsql resets session
/// state when a pooled connection is returned. An empty value is written explicitly when there
/// is no tenant, so a reused connection can never inherit a previous tenant's id.
/// </summary>
public sealed class HouseholdRlsConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    private const string SetConfigSql = "SELECT set_config('app.household_id', @household_id, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = CreateSetConfigCommand(connection);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        using var cmd = CreateSetConfigCommand(connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateSetConfigCommand(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = SetConfigSql;

        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "household_id";
        parameter.Value = tenant.HouseholdId?.ToString() ?? string.Empty;
        cmd.Parameters.Add(parameter);

        return cmd;
    }
}
