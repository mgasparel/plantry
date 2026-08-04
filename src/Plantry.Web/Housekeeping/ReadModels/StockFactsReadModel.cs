using Npgsql;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// Contract for the shared cross-schema read model backing Tidy Up's stock-family detectors — D1
/// (<c>StockUnitUnconvertibleDetector</c>), D3 (<c>StockExpiredDetector</c>), D4
/// (<c>StapleNoLowStockAlertDetector</c>), D6 (<c>MixedIncompatibleUnitsDetector</c>). Extracted as an
/// interface so tests can substitute an in-memory fake (mirrors <c>IMealPlanWeekReadModel</c>).
/// </summary>
public interface IStockFactsReadModel
{
    /// <inheritdoc cref="StockFactsReadModel.LoadAsync"/>
    Task<StockFactsBag> LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// Household-wide cross-schema read model for Tidy Up's stock-family detectors (ADR-021, ADR-024 Phase
/// A). All four stock detectors scan every one of the household's stock records, so — unlike the Meal
/// Planner week page — there is no caller-supplied id set to narrow by; this loads the whole household's
/// stock + the catalog facts those lots reference in a small, flat set of queries whose count is
/// independent of how many products or lots the household has.
///
/// Runs on an RLS-armed connection (ADR-008): every query is household-isolated by Postgres RLS
/// regardless of which context's schema it reaches into, exactly as <c>MealPlanWeekReadModel</c> does.
/// Read-only and one-directional — never writes. Lives in Plantry.Web (the composition root, ADR-021
/// rule 3), the one project that already legitimately references every context.
/// </summary>
public sealed class StockFactsReadModel(
    string connectionString,
    ITenantContext tenant) : IStockFactsReadModel
{
    public async Task<StockFactsBag> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Arm RLS on this connection (mirrors MealPlanWeekReadModel/HouseholdRlsConnectionInterceptor,
        // ADR-008) — always set the GUC, even to empty, so a pooled connection never inherits a
        // previous tenant's app.household_id.
        await using (var armCmd = conn.CreateCommand())
        {
            armCmd.CommandText = "SELECT set_config('app.household_id', @household_id, false)";
            var hidParam = armCmd.CreateParameter();
            hidParam.ParameterName = "household_id";
            hidParam.Value = tenant.HouseholdId?.ToString() ?? string.Empty;
            armCmd.Parameters.Add(hidParam);
            await armCmd.ExecuteNonQueryAsync(ct);
        }

        // ── Query 1: product_stock roots (inventory) — the household's whole stock list ──────────
        var thresholdByProduct = new Dictionary<Guid, decimal?>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT product_id, low_stock_threshold
                FROM inventory.product_stock
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var productId = reader.GetGuid(0);
                var threshold = reader.IsDBNull(1) ? (decimal?)null : reader.GetDecimal(1);
                thresholdByProduct[productId] = threshold;
            }
        }

        if (thresholdByProduct.Count == 0)
            return new StockFactsBag(
                new Dictionary<Guid, StockProductFact>(),
                new Dictionary<Guid, ProductFact>(),
                new Dictionary<Guid, UnitFact>(),
                new Dictionary<Guid, IReadOnlyList<ConversionFact>>());

        // ── Query 2: every stock_entry lot (inventory) — ALL lots, not just active ones. D4 (staple
        // no-low-stock-alert) counts purchase history across active AND depleted lots; D1/D3/D6 filter
        // to IsActive themselves once loaded (mirrors ProductStock.ActiveLotsFefo's own predicate:
        // depleted_at IS NULL AND quantity > 0). ─────────────────────────────────────────────────────
        var entriesByProduct = new Dictionary<Guid, List<StockLotFact>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT entry_id, product_id, unit_id, quantity, expiry_date, purchased_at, depleted_at
                FROM inventory.stock_entry
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var productId = reader.GetGuid(1);
                var entry = new StockLotFact(
                    reader.GetGuid(0),
                    productId,
                    reader.GetGuid(2),
                    reader.GetDecimal(3),
                    reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4)),
                    reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)),
                    IsActive: reader.IsDBNull(6) && reader.GetDecimal(3) > 0m);

                if (!entriesByProduct.TryGetValue(productId, out var list))
                {
                    list = [];
                    entriesByProduct[productId] = list;
                }
                list.Add(entry);
            }
        }

        var stockByProduct = thresholdByProduct.ToDictionary(
            kvp => kvp.Key,
            kvp => new StockProductFact(
                kvp.Key,
                kvp.Value,
                entriesByProduct.TryGetValue(kvp.Key, out var lots) ? lots : []));

        // ── Query 3: products (catalog) — active only, matching ICatalogReadFacade.ListProductsAsync's
        // archived-skip convention every detector's doc comment already documents. ──────────────────
        var productIds = thresholdByProduct.Keys.ToArray();
        var products = new Dictionary<Guid, ProductFact>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, name, track_stock, default_unit_id
                FROM catalog.products
                WHERE id = ANY(@ids) AND archived_at IS NULL
                """;
            var param = cmd.CreateParameter();
            param.ParameterName = "ids";
            param.Value = productIds;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                products[id] = new ProductFact(id, reader.GetString(1), reader.GetBoolean(2), reader.GetGuid(3));
            }
        }

        // ── Query 4: units (catalog) — all household units, small and cacheable. ────────────────────
        var units = new Dictionary<Guid, UnitFact>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, symbol, name, dimension, factor_to_base, is_base
                FROM catalog.units
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                units[id] = new UnitFact(
                    id, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.GetBoolean(5));
            }
        }

        // ── Query 5: product conversions (catalog). ─────────────────────────────────────────────────
        var conversionsByProduct = new Dictionary<Guid, List<ConversionFact>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT product_id, from_unit_id, to_unit_id, factor
                FROM catalog.product_conversions
                WHERE product_id = ANY(@ids)
                """;
            var param = cmd.CreateParameter();
            param.ParameterName = "ids";
            param.Value = productIds;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var productId = reader.GetGuid(0);
                var fact = new ConversionFact(productId, reader.GetGuid(1), reader.GetGuid(2), reader.GetDecimal(3));
                if (!conversionsByProduct.TryGetValue(productId, out var list))
                {
                    list = [];
                    conversionsByProduct[productId] = list;
                }
                list.Add(fact);
            }
        }

        return new StockFactsBag(
            stockByProduct,
            products,
            units,
            conversionsByProduct.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<ConversionFact>)kvp.Value));
    }
}

// ── Data bag ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Flat in-memory bag of raw inputs shared by Tidy Up's stock-family detectors (D1/D3/D4/D6).
/// Read-only after construction; the detectors run their own pure math over this bag with no further
/// round-trips (ADR-021 rule 1).
/// </summary>
public sealed class StockFactsBag(
    IReadOnlyDictionary<Guid, StockProductFact> stockByProduct,
    IReadOnlyDictionary<Guid, ProductFact> products,
    IReadOnlyDictionary<Guid, UnitFact> units,
    IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> conversionsByProduct)
{
    public IReadOnlyDictionary<Guid, StockProductFact> StockByProduct { get; } = stockByProduct;
    public IReadOnlyDictionary<Guid, ProductFact> Products { get; } = products;
    public IReadOnlyDictionary<Guid, UnitFact> Units { get; } = units;
    public IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> ConversionsByProduct { get; } = conversionsByProduct;

    /// <summary>Builds the shared unit-conversion delegate over this bag's Units/ConversionsByProduct (see
    /// <c>HousekeepingConversions.BuildConverter</c>).</summary>
    public Func<Guid, decimal, Guid, Guid, Plantry.SharedKernel.Result<decimal>> BuildConverter() =>
        HousekeepingConversions.BuildConverter(Units, ConversionsByProduct);
}

// ── Fact records ─────────────────────────────────────────────────────────────────────────────────

/// <summary>One household's stock for one product — the <c>inventory.product_stock</c> root plus its
/// <c>inventory.stock_entry</c> lots, ALL of them (active and depleted alike; see <see cref="StockLotFact.IsActive"/>).</summary>
public sealed record StockProductFact(
    Guid ProductId,
    decimal? LowStockThreshold,
    IReadOnlyList<StockLotFact> Entries);

/// <summary>One lot from <c>inventory.stock_entry</c>. <see cref="IsActive"/> mirrors
/// <c>StockEntry.IsActive</c> exactly: <c>depleted_at IS NULL AND quantity &gt; 0</c>.</summary>
public sealed record StockLotFact(
    Guid EntryId,
    Guid ProductId,
    Guid UnitId,
    decimal Quantity,
    DateOnly? ExpiryDate,
    DateOnly? PurchasedAt,
    bool IsActive);
