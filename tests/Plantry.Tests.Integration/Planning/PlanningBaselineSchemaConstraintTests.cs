using Npgsql;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Planning;

/// <summary>
/// Schema-level regression coverage (plantry-g3da.8 pass-1 critic finding) for the six composite
/// tenancy FKs, the superseded single-column shopping FK drop, the domain CHECK constraints, the
/// contribution-source column default, and the twelve per-table RLS policies that
/// <c>Migrations/Planning/20260808180000_InitialPlanningSchema.cs</c> re-homes as raw SQL. All of
/// these are deliberately absent from <see cref="Plantry.Planning.Infrastructure.PlanningDbContext"/>'s
/// fluent model and from <c>PlanningDbContextModelSnapshot</c> (they were never part of the EF model in
/// the pre-squash MealPlanningDbContext/ShoppingDbContext pair either), which means nothing else in the
/// solution notices if the raw-SQL re-homing silently regresses — the same gap plantry-g3da.10's pass 1
/// shipped for Pantry, closed in its pass 2 by the sibling
/// <c>tests/Plantry.Tests.Integration/Pantry/PantryBaselineSchemaConstraintTests.cs</c> this file
/// mirrors. Queries live catalog metadata (<c>pg_constraint</c>/<c>pg_policies</c>/
/// <c>information_schema.columns</c>) rather than asserting behaviourally, because the composite FKs
/// and RLS policies have no in-process EF code path that would otherwise exercise them — the whole
/// point is that Postgres enforces them independently of the app.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PlanningBaselineSchemaConstraintTests(PostgresFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    public static IEnumerable<object[]> CompositeConstraints => new[]
    {
        // name, table (schema-qualified), contype, expected ordered columns, confdeltype
        new object[]
        {
            "fk_meal_slot_config_composite", "meal_planning.meal_slot", "f",
            new[] { "household_id", "meal_slot_config_id" }, "c",
        },
        new object[]
        {
            "fk_planned_meal_plan_composite", "meal_planning.planned_meal", "f",
            new[] { "household_id", "meal_plan_id" }, "c",
        },
        new object[]
        {
            "fk_planned_meal_slot_composite", "meal_planning.planned_meal", "f",
            new[] { "household_id", "meal_slot_id" }, "r",
        },
        new object[]
        {
            "fk_planned_dish_meal_composite", "meal_planning.planned_dish", "f",
            new[] { "household_id", "planned_meal_id" }, "c",
        },
        new object[]
        {
            "fk_tag_stance_preference_composite", "meal_planning.tag_stance", "f",
            new[] { "household_id", "user_preference_id" }, "c",
        },
        new object[]
        {
            "fk_shopping_list_item_shopping_list", "shopping.shopping_list_item", "f",
            new[] { "household_id", "shopping_list_id" }, "c",
        },
    };

    [Theory(DisplayName = "Composite tenancy FK exists with the exact column order and confdeltype")]
    [MemberData(nameof(CompositeConstraints))]
    public async Task CompositeConstraint_Exists(
        string constraintName, string qualifiedTable, string contype, string[] expectedColumns, string confdeltype)
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
        Assert.Equal(confdeltype, reader.IsDBNull(1) ? null : reader.GetString(1));
        var actualColumns = (string[])reader.GetValue(2);
        Assert.Equal(expectedColumns, actualColumns);
    }

    [Fact(DisplayName = "Superseded single-column shopping_list_item FK is absent — the raw-SQL drop-then-recreate actually ran")]
    public async Task SupersededSingleColumnConstraint_IsAbsent()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_constraint WHERE conname = $1 AND conrelid = $2::regclass";
        cmd.Parameters.AddWithValue("FK_shopping_list_item_shopping_list_shopping_list_id");
        cmd.Parameters.AddWithValue("shopping.shopping_list_item");

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync(), "Superseded constraint FK_shopping_list_item_shopping_list_shopping_list_id is still present.");
    }

    public static IEnumerable<object[]> CheckConstraints => new[]
    {
        new object[] { "ck_planned_meal_source", "meal_planning.planned_meal" },
        new object[] { "ck_tag_stance_value", "meal_planning.tag_stance" },
        new object[] { "ck_shopping_list_item_product_or_free_text", "shopping.shopping_list_item" },
        new object[] { "ck_contribution_source", "shopping.shopping_list_item_contribution" },
        new object[] { "ck_planned_dish_shape", "meal_planning.planned_dish" },
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

    [Fact(DisplayName = "shopping_list_item_contribution.source carries the 'manual' column default")]
    public async Task ContributionSourceColumnDefault_IsManual()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT column_default FROM information_schema.columns
            WHERE table_schema = 'shopping' AND table_name = 'shopping_list_item_contribution'
              AND column_name = 'source'
            """;

        var columnDefault = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("'manual'::character varying", columnDefault);
    }

    public static IEnumerable<object[]> PolicedTables => new[]
    {
        new object[] { "meal_planning", "meal_plan" },
        new object[] { "meal_planning", "planned_meal" },
        new object[] { "meal_planning", "planned_dish" },
        new object[] { "meal_planning", "meal_slot_config" },
        new object[] { "meal_planning", "meal_slot" },
        new object[] { "meal_planning", "user_preference" },
        new object[] { "meal_planning", "tag_stance" },
        new object[] { "meal_planning", "household_planning_settings" },
        new object[] { "meal_planning", "week_planning_override" },
        new object[] { "shopping", "shopping_list" },
        new object[] { "shopping", "shopping_list_item" },
        new object[] { "shopping", "shopping_list_item_contribution" },
    };

    [Theory(DisplayName = "household_isolation RLS policy exists on the expected table")]
    [MemberData(nameof(PolicedTables))]
    public async Task HouseholdIsolationPolicy_Exists(string schema, string table)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM pg_policies
            WHERE schemaname = $1 AND tablename = $2 AND policyname = 'household_isolation'
            """;
        cmd.Parameters.AddWithValue(schema);
        cmd.Parameters.AddWithValue(table);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"household_isolation policy not found on {schema}.{table}.");
    }

    [Fact(DisplayName = "shopping_list_item_contribution's policy is parent-scoped through shopping_list_item")]
    public async Task ContributionPolicy_IsScopedThroughShoppingListItem()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT qual FROM pg_policies
            WHERE schemaname = 'shopping' AND tablename = 'shopping_list_item_contribution'
              AND policyname = 'household_isolation'
            """;

        var qual = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(qual);
        Assert.Contains("shopping_list_item", qual);
    }
}
