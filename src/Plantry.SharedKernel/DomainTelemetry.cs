using System.Diagnostics.Metrics;

namespace Plantry.SharedKernel;

/// <summary>
/// Central metrics primitives for the Plantry domain layer (non-AI).
/// <para>
/// Counters cover the three core domain operations — intake sessions committed, stock consume
/// operations, and recipes cooked — plus a low-stock event counter that fires whenever a consume
/// drops a product to or below its configured threshold. All counters are under the
/// <c>Plantry.Domain</c> meter, which must be registered via
/// <c>AddMeter(DomainTelemetry.MeterName)</c> in ServiceDefaults.
/// </para>
/// <para>
/// <strong>PII guard:</strong> no household names, product names, user identifiers, or other
/// personal data appear as metric attributes — only aggregate counters (Gate 9).
/// </para>
/// </summary>
public static class DomainTelemetry
{
    /// <summary>
    /// Meter name. Register in <c>ServiceDefaults.ConfigureOpenTelemetry</c> via
    /// <c>.AddMeter(DomainTelemetry.MeterName)</c>.
    /// </summary>
    public const string MeterName = "Plantry.Domain";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    /// Incremented each time an intake session is successfully committed (all confirmed lines
    /// written to Catalog + Inventory + Pricing). Query as <c>plantry.intake.sessions_committed</c>.
    /// </summary>
    public static readonly Counter<long> IntakeSessionsCommitted =
        Meter.CreateCounter<long>(
            "plantry.intake.sessions_committed",
            unit: "{session}",
            description: "Number of intake sessions successfully committed to the pantry.");

    /// <summary>
    /// Incremented each time a stock consume operation completes successfully — covers manual
    /// consumes from the Pantry UI, recipe cooks, and programmatic calls. Query as
    /// <c>plantry.inventory.stock_consumed</c>.
    /// </summary>
    public static readonly Counter<long> StockConsumed =
        Meter.CreateCounter<long>(
            "plantry.inventory.stock_consumed",
            unit: "{operation}",
            description: "Number of successful stock consume operations across all sources.");

    /// <summary>
    /// Incremented each time a recipe cook completes (even with shortfalls — shortfalls never
    /// block a cook). Query as <c>plantry.recipes.cooked</c>.
    /// </summary>
    public static readonly Counter<long> RecipesCooked =
        Meter.CreateCounter<long>(
            "plantry.recipes.cooked",
            unit: "{cook}",
            description: "Number of recipes cooked.");

    /// <summary>
    /// Incremented each time a stock consume drops a product to or below its configured
    /// low-stock threshold. Fires at most once per consume operation per product. Query as
    /// <c>plantry.inventory.low_stock_events</c>.
    /// <para>
    /// <strong>Note:</strong> the check uses a raw sum of active-lot quantities in their stored
    /// units, which is accurate when all lots share the product's display unit (the common case).
    /// Mixed-unit stocks may over- or under-count; a precision alarm belongs in a dedicated
    /// service. This counter is a best-effort operational signal.
    /// </para>
    /// </summary>
    public static readonly Counter<long> LowStockEvents =
        Meter.CreateCounter<long>(
            "plantry.inventory.low_stock_events",
            unit: "{event}",
            description: "Number of times a stock consume caused a product to cross its low-stock threshold.");
}
