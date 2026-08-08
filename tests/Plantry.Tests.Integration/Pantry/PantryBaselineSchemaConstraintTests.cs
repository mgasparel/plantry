using Npgsql;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Pantry;

/// <summary>
/// Schema-level regression coverage (plantry-g3da.10 pass-2 critic finding) for the six composite
/// tenancy constraints, the CHECK-constraint allow-lists, and the unordered-pair expression index
/// that <c>Migrations/Pantry/20260808165152_InitialPantrySchema.cs</c> re-homes as raw SQL. All of
/// these are deliberately absent from <see cref="Plantry.Pantry.Infrastructure.PantryDbContext"/>'s
/// fluent model and from <c>PantryDbContextModelSnapshot</c> (they were never part of the EF model in
/// the pre-squash CatalogDbContext/InventoryDbContext pair either — see the migration's own
/// comments), which means nothing else in the solution notices if the raw-SQL re-homing silently
/// regresses, the way pass 1 of this ticket did. Queries live catalog metadata
/// (<c>pg_constraint</c>/<c>pg_indexes</c>) rather than asserting behaviourally, because several of
/// these constraints (the composite FKs in particular) have no in-process EF code path that would
/// otherwise exercise them — the whole point is that Postgres enforces them independently of the app.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PantryBaselineSchemaConstraintTests(PostgresFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    public static IEnumerable<object[]> CompositeConstraints => new[]
    {
        // name, table (schema-qualified), contype, expected ordered columns, confdeltype (null for non-FK)
        new object[] { "AK_products_household_id_id", "catalog.products", "u", new[] { "household_id", "id" }, null! },
        new object[]
        {
            "FK_products_products_household_id_parent_product_id", "catalog.products", "f",
            new[] { "household_id", "parent_product_id" }, "a",
        },
        new object[]
        {
            "FK_product_conversions_products_household_id_product_id", "catalog.product_conversions", "f",
            new[] { "household_id", "product_id" }, "c",
        },
        new object[]
        {
            "FK_product_skus_products_household_id_product_id", "catalog.product_skus", "f",
            new[] { "household_id", "product_id" }, "c",
        },
        new object[] { "uq_stock_entry_household_entry", "inventory.stock_entry", "u", new[] { "household_id", "entry_id" }, null! },
        new object[]
        {
            "fk_stock_journal_entry_stock_entry", "inventory.stock_journal_entry", "f",
            new[] { "household_id", "entry_id" }, "a",
        },
    };

    [Theory(DisplayName = "Composite tenancy constraint exists with the exact column order (and, for FKs, confdeltype)")]
    [MemberData(nameof(CompositeConstraints))]
    public async Task CompositeConstraint_Exists(
        string constraintName, string qualifiedTable, string contype, string[] expectedColumns, string? confdeltype)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.contype::text, c.confdeltype::text,
                   array_agg(a.attname ORDER BY k.ord) AS columns
            FROM pg_constraint c
            JOIN unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
            WHERE c.conname = $1 AND c.conrelid = $2::regclass
            GROUP BY c.contype, c.confdeltype
            """;
        cmd.Parameters.AddWithValue(constraintName);
        cmd.Parameters.AddWithValue(qualifiedTable);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Constraint {constraintName} not found on {qualifiedTable}.");

        Assert.Equal(contype, reader.GetString(0));
        var actualColumns = (string[])reader.GetValue(2);
        Assert.Equal(expectedColumns, actualColumns);

        if (confdeltype is not null)
            Assert.Equal(confdeltype, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public static IEnumerable<object[]> SupersededSingleColumnConstraints => new[]
    {
        new object[] { "FK_product_conversions_products_product_id", "catalog.product_conversions" },
        new object[] { "FK_product_skus_products_product_id", "catalog.product_skus" },
        new object[] { "FK_stock_journal_entry_stock_entry_entry_id", "inventory.stock_journal_entry" },
    };

    [Theory(DisplayName = "Superseded single-column FK is absent — the raw-SQL drop-then-recreate actually ran")]
    [MemberData(nameof(SupersededSingleColumnConstraints))]
    public async Task SupersededSingleColumnConstraint_IsAbsent(string constraintName, string qualifiedTable)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_constraint WHERE conname = $1 AND conrelid = $2::regclass";
        cmd.Parameters.AddWithValue(constraintName);
        cmd.Parameters.AddWithValue(qualifiedTable);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync(), $"Superseded constraint {constraintName} is still present on {qualifiedTable}.");
    }

    [Fact(DisplayName = "Unordered-pair unique expression index exists on product_conversions")]
    public async Task UnorderedPairExpressionIndex_Exists()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'catalog' AND tablename = 'product_conversions'
              AND indexname = 'ix_product_conversions_product_unordered_pair'
            """;

        var indexDef = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(indexDef);
        Assert.Contains("LEAST", indexDef);
        Assert.Contains("GREATEST", indexDef);
    }

    public static IEnumerable<object[]> CheckConstraints => new[]
    {
        new object[] { "CK_locations_type", "catalog.locations" },
        new object[] { "ck_products_no_self_parent", "catalog.products" },
        new object[] { "ck_product_conversions_source", "catalog.product_conversions" },
        new object[] { "ck_units_display_style", "catalog.units" },
        new object[] { "ck_units_unit_system", "catalog.units" },
        new object[] { "ck_stock_journal_entry_reason", "inventory.stock_journal_entry" },
        new object[] { "ck_stock_journal_entry_source_type", "inventory.stock_journal_entry" },
    };

    [Theory(DisplayName = "CHECK constraint exists on the expected table")]
    [MemberData(nameof(CheckConstraints))]
    public async Task CheckConstraint_Exists(string constraintName, string qualifiedTable)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT contype::text FROM pg_constraint WHERE conname = $1 AND conrelid = $2::regclass";
        cmd.Parameters.AddWithValue(constraintName);
        cmd.Parameters.AddWithValue(qualifiedTable);

        var contype = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("c", contype);
    }
}
