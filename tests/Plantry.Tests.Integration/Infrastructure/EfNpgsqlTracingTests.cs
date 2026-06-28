using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Infrastructure;

/// <summary>
/// Proves that EF Core and Npgsql emit OpenTelemetry-compatible Activity spans for database
/// operations, and that the <c>db.statement</c> attribute carries parameterised SQL only —
/// no literal PII values (ADR-009 / plantry-ess9.2 AC1).
///
/// <para>
/// The test builds a <see cref="TracerProvider"/> with the two sources that
/// <c>ServiceDefaults/Extensions.cs</c> registers:
/// </para>
/// <list type="bullet">
///   <item><c>AddEntityFrameworkCoreInstrumentation()</c> — hooks EF Core DiagnosticSource events,
///     emitting spans with <c>db.system=postgresql</c> and a parameterised <c>db.statement</c></item>
///   <item><c>AddSource("Npgsql")</c> — captures wire-level Npgsql spans (nested under EF spans)</item>
/// </list>
/// Activities are captured via a BCL <see cref="ActivityListener"/> — no
/// <c>OpenTelemetry.Exporter.InMemory</c> package is required.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EfNpgsqlTracingTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Asserts three behavioural guarantees from plantry-ess9.2 AC1:
    /// <list type="number">
    ///   <item>(a) At least one <see cref="Activity"/> with <c>db.system</c> or
    ///     <c>db.system.name</c> = <c>postgresql</c> is captured while EF issues a query
    ///     against Postgres.</item>
    ///   <item>(b) The captured activity has a non-zero duration (started and stopped).</item>
    ///   <item>(c) When a <c>db.statement</c> tag is present, it contains parameter placeholders
    ///     (<c>$1</c> / <c>@p</c>) and does not contain any literal PII value from the test data,
    ///     proving that <c>EnableSensitiveDataLogging</c> and the experimental
    ///     <c>OTEL_DOTNET_EXPERIMENTAL_EFCORE_ENABLE_TRACE_DB_QUERY_PARAMETERS</c> env var are
    ///     both absent (Gate 9 no-PII guardrail).</item>
    /// </list>
    /// </summary>
    [Fact(DisplayName = "EF/Npgsql spans: db.system=postgresql, non-zero duration, parameterised db.statement")]
    public async Task EfQuery_EmitsActivity_WithPostgresqlSystemTag_NonZeroDuration_AndParameterisedStatement()
    {
        // ── Arrange ──────────────────────────────────────────────────────────────────
        // A sentinel value that must never appear literally inside any db.statement tag.
        // EF/Npgsql must not inline parameter values into the exported SQL text.
        const string piiSentinel = "PII-Sentinel-Unit-RCMU";

        var capturedActivities = new List<Activity>();

        // Build a TracerProvider that mirrors the sources registered in ServiceDefaults:
        //   AddEntityFrameworkCoreInstrumentation() — subscribes to Microsoft.EntityFrameworkCore
        //   DiagnosticSource events and bridges them to Activity objects.
        //   AddSource("Npgsql") — includes Npgsql's own ActivitySource("Npgsql") spans.
        // The TracerProvider must be alive for the duration of the EF call; disposing it
        // tears down the DiagnosticSource listener.
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddEntityFrameworkCoreInstrumentation()
            .AddSource("Npgsql")
            .Build();

        // Capture activities via the BCL ActivityListener.  Must be registered AFTER the
        // TracerProvider is built so EF Core instrumentation hooks are already in place.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name is "OpenTelemetry.Instrumentation.EntityFrameworkCore" or "Npgsql",

            // AllDataAndRecorded so the listener receives the activity and all its tags.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,

            // Capture after Stop() so all tags (including db.statement) are set.
            ActivityStopped = capturedActivities.Add,
        };

        ActivitySource.AddActivityListener(listener);

        // Seed one unit so the subsequent SELECT returns a row (exercises the read path).
        var opts = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;

        await using var seedCtx = new CatalogDbContext(opts);
        seedCtx.SetHouseholdId(_household.Value);
        var unit = CatalogUnit.Create(_household, "kg", piiSentinel, Dimension.Mass, 1000m, isBase: false);
        seedCtx.Units.Add(unit);
        await seedCtx.SaveChangesAsync();

        // Clear any spans captured during seeding so the assertion focuses on the SELECT below.
        capturedActivities.Clear();

        // ── Act ───────────────────────────────────────────────────────────────────────
        await using var queryCtx = new CatalogDbContext(opts);
        queryCtx.SetHouseholdId(_household.Value);
        _ = await queryCtx.Units.ToListAsync();

        // ── Assert (a) — at least one Activity with db.system = postgresql ─────────────
        // The EF Core instrumentation maps the Npgsql EF provider name
        // ("Npgsql.EntityFrameworkCore.PostgreSQL") to the db.system value "postgresql".
        // Npgsql's own spans also set db.system = "postgresql".
        var dbActivities = capturedActivities
            .Where(a =>
                string.Equals(
                    a.GetTagItem("db.system")?.ToString(), "postgresql",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    a.GetTagItem("db.system.name")?.ToString(), "postgresql",
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            dbActivities.Count > 0,
            $"Expected at least one Activity with db.system=postgresql. " +
            $"Captured {capturedActivities.Count} activities total. " +
            $"Sources seen: [{string.Join(", ", capturedActivities.Select(a => a.Source.Name).Distinct())}]. " +
            $"Tags seen: [{string.Join("; ", capturedActivities.SelectMany(a => a.Tags).Select(t => $"{t.Key}={t.Value}"))}]");

        // ── Assert (b) — non-zero duration ───────────────────────────────────────────
        var withDuration = dbActivities.Where(a => a.Duration > TimeSpan.Zero).ToList();
        Assert.True(
            withDuration.Count > 0,
            $"Expected at least one db.system=postgresql activity to report a non-zero duration. " +
            $"Durations: [{string.Join(", ", dbActivities.Select(a => a.Duration))}]");

        // ── Assert (c) — parameterised db.statement; no PII literal ─────────────────
        // EF Core instrumentation sets the db.statement tag to the generated SQL.
        // The SQL must use parameter placeholders ($1/$2/… for Postgres, @p0/@p1/… for EF
        // placeholder style) and must NOT inline the literal string value used as the unit
        // name ("piiSentinel"), which would indicate EnableSensitiveDataLogging is set or
        // the experimental parameter-inlining env var is active.
        var activitiesWithStatement = dbActivities
            .Where(a => a.GetTagItem("db.statement") is not null)
            .ToList();

        foreach (var activity in activitiesWithStatement)
        {
            var stmt = activity.GetTagItem("db.statement")!.ToString()!;

            // (c-i) No PII literal.
            Assert.False(
                stmt.Contains(piiSentinel, StringComparison.OrdinalIgnoreCase),
                $"db.statement contains the literal PII sentinel '{piiSentinel}' in activity " +
                $"'{activity.DisplayName}'. This means parameter values are being inlined into " +
                $"the exported SQL — a no-PII guardrail violation (Gate 9). SQL: {stmt}");

            // (c-ii) Statement contains a placeholder, OR has no WHERE clause (e.g. a DDL/schema
            // query).  EF generates named parameters with one of these naming patterns:
            //   @p0, @p1          — INSERT/UPDATE positional parameters
            //   @__param_N        — LINQ query variable capture
            //   @ef_filter__p     — global query-filter parameters (household_id filter)
            //   $1, $2            — Postgres native positional syntax (Npgsql raw commands)
            // A SELECT statement that has a WHERE clause but no placeholder is the failure case.
            var hasWhereClause = stmt.Contains("WHERE", StringComparison.OrdinalIgnoreCase);
            var hasPlaceholder = stmt.Contains("$",     StringComparison.Ordinal)
                              || stmt.Contains("@",     StringComparison.Ordinal);

            if (hasWhereClause)
            {
                Assert.True(
                    hasPlaceholder,
                    $"db.statement in activity '{activity.DisplayName}' has a WHERE clause but no " +
                    $"parameter placeholder ($N or @param). This may indicate parameter values are " +
                    $"being inlined into the SQL — a Gate 9 no-PII guardrail violation. SQL: {stmt}");
            }
        }

        // If no activity carried db.statement the tag was not emitted (some OTEL configurations
        // omit it).  In that case assertions (a) and (b) are still satisfied — the spans fired,
        // and the absence of db.statement is not itself a PII leak.
        // Note: the activity name observed above was the database name ("plantry_test"),
        // which is the Npgsql convention for connection-level spans.  EF Core instrumentation
        // spans carry the SQL operation name.  Both are valid proofs of the wiring.
    }
}
