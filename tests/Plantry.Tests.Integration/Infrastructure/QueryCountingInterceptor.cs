using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Plantry.Tests.Integration.Infrastructure;

/// <summary>
/// Records the SQL command text of every reader command EF Core issues against a
/// <c>DbContext</c> it's wired into, so a test can assert on how many queries — and which
/// ones — a piece of code issues (plantry-jefp).
///
/// <para>
/// Wire it in via <c>DbContextOptionsBuilder.AddInterceptors(counter)</c> when constructing the
/// context under test:
/// </para>
/// <code>
/// var counter = new QueryCountingInterceptor();
/// var ctx = new CatalogDbContext(
///     new DbContextOptionsBuilder&lt;CatalogDbContext&gt;()
///         .UseNpgsql(db.ConnectionString)
///         .AddInterceptors(counter)
///         .Options);
/// </code>
///
/// <para>
/// Reusable across any EF-backed integration test that wants a query-count assertion, not
/// specific to any one adapter or bounded context.
/// </para>
///
/// <para>
/// <b>Single-threaded test use only.</b> <see cref="Commands"/> is a plain <see cref="List{T}"/>
/// with no locking — this interceptor is meant for one test issuing queries sequentially against
/// one context, not for asserting across concurrent/parallel query execution.
/// </para>
/// </summary>
public sealed class QueryCountingInterceptor : DbCommandInterceptor
{
    private readonly List<string> _commands = [];

    /// <summary>The <c>CommandText</c> of every reader command recorded so far, in issue order.</summary>
    public IReadOnlyList<string> Commands => _commands;

    /// <summary>Total number of reader commands recorded so far.</summary>
    public int Count => _commands.Count;

    /// <summary>
    /// Number of recorded commands whose <c>CommandText</c> contains <paramref name="fragment"/>
    /// (case-insensitive substring match) — e.g. <c>CountMatching("units")</c> to count SELECTs
    /// against <c>catalog.units</c>.
    /// </summary>
    public int CountMatching(string fragment) =>
        _commands.Count(c => c.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Clears all recorded commands so a test can re-baseline mid-run.</summary>
    public void Reset() => _commands.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        _commands.Add(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Add(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
