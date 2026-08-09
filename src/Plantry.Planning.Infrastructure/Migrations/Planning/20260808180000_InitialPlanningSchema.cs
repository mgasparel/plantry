using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Planning.Infrastructure.Migrations.Planning
{
    /// <inheritdoc />
    public partial class InitialPlanningSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "meal_planning");

            migrationBuilder.EnsureSchema(
                name: "shopping");

            migrationBuilder.CreateTable(
                name: "household_planning_settings",
                schema: "meal_planning",
                columns: table => new
                {
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    budget_minor_units = table.Column<long>(type: "bigint", nullable: true),
                    budget_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    weights_waste = table.Column<int>(type: "integer", nullable: true),
                    weights_cost = table.Column<int>(type: "integer", nullable: true),
                    weights_variety = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_household_planning_settings", x => x.household_id);
                });

            migrationBuilder.CreateTable(
                name: "meal_plan",
                schema: "meal_planning",
                columns: table => new
                {
                    meal_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meal_plan", x => x.meal_plan_id);
                });

            migrationBuilder.CreateTable(
                name: "meal_slot_config",
                schema: "meal_planning",
                columns: table => new
                {
                    meal_slot_config_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meal_slot_config", x => x.meal_slot_config_id);
                });

            migrationBuilder.CreateTable(
                name: "shopping_list",
                schema: "shopping",
                columns: table => new
                {
                    shopping_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shopping_list", x => x.shopping_list_id);
                });

            migrationBuilder.CreateTable(
                name: "user_preference",
                schema: "meal_planning",
                columns: table => new
                {
                    user_preference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_preference", x => x.user_preference_id);
                });

            migrationBuilder.CreateTable(
                name: "week_planning_override",
                schema: "meal_planning",
                columns: table => new
                {
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    budget_minor_units = table.Column<long>(type: "bigint", nullable: true),
                    budget_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    weights_waste = table.Column<int>(type: "integer", nullable: true),
                    weights_cost = table.Column<int>(type: "integer", nullable: true),
                    weights_variety = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_week_planning_override", x => new { x.household_id, x.week_start });
                });

            migrationBuilder.CreateTable(
                name: "meal_slot",
                schema: "meal_planning",
                columns: table => new
                {
                    meal_slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meal_slot_config_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    default_attendees = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    include_in_auto_plan = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meal_slot", x => x.meal_slot_id);
                    table.ForeignKey(
                        name: "FK_meal_slot_meal_slot_config_meal_slot_config_id",
                        column: x => x.meal_slot_config_id,
                        principalSchema: "meal_planning",
                        principalTable: "meal_slot_config",
                        principalColumn: "meal_slot_config_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planned_meal",
                schema: "meal_planning",
                columns: table => new
                {
                    planned_meal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meal_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    meal_slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    attendees_override = table.Column<List<Guid>>(type: "uuid[]", nullable: true),
                    reasoning = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planned_meal", x => x.planned_meal_id);
                    table.ForeignKey(
                        name: "FK_planned_meal_meal_plan_meal_plan_id",
                        column: x => x.meal_plan_id,
                        principalSchema: "meal_planning",
                        principalTable: "meal_plan",
                        principalColumn: "meal_plan_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shopping_list_item",
                schema: "shopping",
                columns: table => new
                {
                    shopping_list_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shopping_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    free_text = table.Column<string>(type: "text", nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shopping_list_item", x => x.shopping_list_item_id);
                    table.ForeignKey(
                        name: "FK_shopping_list_item_shopping_list_shopping_list_id",
                        column: x => x.shopping_list_id,
                        principalSchema: "shopping",
                        principalTable: "shopping_list",
                        principalColumn: "shopping_list_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tag_stance",
                schema: "meal_planning",
                columns: table => new
                {
                    tag_stance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_preference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stance = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_stance", x => x.tag_stance_id);
                    table.ForeignKey(
                        name: "FK_tag_stance_user_preference_user_preference_id",
                        column: x => x.user_preference_id,
                        principalSchema: "meal_planning",
                        principalTable: "user_preference",
                        principalColumn: "user_preference_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planned_dish",
                schema: "meal_planning",
                columns: table => new
                {
                    planned_dish_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_meal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    servings = table.Column<int>(type: "integer", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planned_dish", x => x.planned_dish_id);
                    table.CheckConstraint(
                        "ck_planned_dish_shape",
                        "((recipe_id IS NOT NULL AND product_id IS NULL AND servings IS NOT NULL AND servings >= 1 AND quantity IS NULL AND unit_id IS NULL) OR " +
                        "(recipe_id IS NULL AND product_id IS NOT NULL AND servings IS NULL AND quantity IS NOT NULL AND quantity > 0 AND unit_id IS NOT NULL AND unit_id <> '00000000-0000-0000-0000-000000000000'))");
                    table.ForeignKey(
                        name: "FK_planned_dish_planned_meal_planned_meal_id",
                        column: x => x.planned_meal_id,
                        principalSchema: "meal_planning",
                        principalTable: "planned_meal",
                        principalColumn: "planned_meal_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shopping_list_item_contribution",
                schema: "shopping",
                columns: table => new
                {
                    contribution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shopping_list_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shopping_list_item_contribution", x => x.contribution_id);
                    table.ForeignKey(
                        name: "FK_shopping_list_item_contribution_shopping_list_item_shopping~",
                        column: x => x.shopping_list_item_id,
                        principalSchema: "shopping",
                        principalTable: "shopping_list_item",
                        principalColumn: "shopping_list_item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_meal_plan_household_id",
                schema: "meal_planning",
                table: "meal_plan",
                columns: new[] { "household_id", "meal_plan_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_meal_plan_household_week",
                schema: "meal_planning",
                table: "meal_plan",
                columns: new[] { "household_id", "week_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_meal_slot_config_household",
                schema: "meal_planning",
                table: "meal_slot_config",
                column: "household_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_meal_slot_config_household_id",
                schema: "meal_planning",
                table: "meal_slot_config",
                columns: new[] { "household_id", "meal_slot_config_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_shopping_list_household_list",
                schema: "shopping",
                table: "shopping_list",
                columns: new[] { "household_id", "shopping_list_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_user_preference_household_id",
                schema: "meal_planning",
                table: "user_preference",
                columns: new[] { "household_id", "user_preference_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_user_preference_household_user",
                schema: "meal_planning",
                table: "user_preference",
                columns: new[] { "household_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meal_slot_meal_slot_config_id",
                schema: "meal_planning",
                table: "meal_slot",
                column: "meal_slot_config_id");

            migrationBuilder.CreateIndex(
                name: "ux_meal_slot_household_id",
                schema: "meal_planning",
                table: "meal_slot",
                columns: new[] { "household_id", "meal_slot_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_planned_meal_household_id",
                schema: "meal_planning",
                table: "planned_meal",
                columns: new[] { "household_id", "planned_meal_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_planned_meal_plan_date_slot_ordinal",
                schema: "meal_planning",
                table: "planned_meal",
                columns: new[] { "meal_plan_id", "date", "meal_slot_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shopping_list_item_household_list",
                schema: "shopping",
                table: "shopping_list_item",
                columns: new[] { "household_id", "shopping_list_id" });

            migrationBuilder.CreateIndex(
                name: "IX_shopping_list_item_shopping_list_id",
                schema: "shopping",
                table: "shopping_list_item",
                column: "shopping_list_id");

            migrationBuilder.CreateIndex(
                name: "ux_tag_stance_pref_tag",
                schema: "meal_planning",
                table: "tag_stance",
                columns: new[] { "user_preference_id", "tag_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_planned_dish_meal_ordinal",
                schema: "meal_planning",
                table: "planned_dish",
                columns: new[] { "planned_meal_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shopping_list_item_contribution_item",
                schema: "shopping",
                table: "shopping_list_item_contribution",
                column: "shopping_list_item_id");

            // ── Tenant-safe composite FKs (ADR-008 / conventions.md) ────────────────
            // EF adds simple PK-based FKs above; we add COMPOSITE FK anchors so every child row is tied
            // to BOTH the PK and household_id, preventing cross-household FK bypass. Re-homed unchanged
            // from the former MealPlanningDbContext's InitialMealPlanningSchema migration (plantry-g3da.8
            // squash — see PlanningDbContext remarks). These coexist with the simple EF-managed FKs above
            // (the composite constraints are additive, not replacements, exactly as in the original schema).
            migrationBuilder.Sql(@"
                ALTER TABLE meal_planning.meal_slot
                    ADD CONSTRAINT fk_meal_slot_config_composite
                    FOREIGN KEY (household_id, meal_slot_config_id)
                    REFERENCES meal_planning.meal_slot_config (household_id, meal_slot_config_id)
                    ON DELETE CASCADE;

                ALTER TABLE meal_planning.planned_meal
                    ADD CONSTRAINT fk_planned_meal_plan_composite
                    FOREIGN KEY (household_id, meal_plan_id)
                    REFERENCES meal_planning.meal_plan (household_id, meal_plan_id)
                    ON DELETE CASCADE;

                -- Within-context FK: planned_meal → meal_slot ON DELETE RESTRICT (M10; slots are
                -- soft-archived, never physically removed, so RESTRICT never fires in practice).
                ALTER TABLE meal_planning.planned_meal
                    ADD CONSTRAINT fk_planned_meal_slot_composite
                    FOREIGN KEY (household_id, meal_slot_id)
                    REFERENCES meal_planning.meal_slot (household_id, meal_slot_id)
                    ON DELETE RESTRICT;

                ALTER TABLE meal_planning.planned_dish
                    ADD CONSTRAINT fk_planned_dish_meal_composite
                    FOREIGN KEY (household_id, planned_meal_id)
                    REFERENCES meal_planning.planned_meal (household_id, planned_meal_id)
                    ON DELETE CASCADE;

                ALTER TABLE meal_planning.tag_stance
                    ADD CONSTRAINT fk_tag_stance_preference_composite
                    FOREIGN KEY (household_id, user_preference_id)
                    REFERENCES meal_planning.user_preference (household_id, user_preference_id)
                    ON DELETE CASCADE;
            ");

            // Upgrade the EF-created single-column FK to a composite (household_id, shopping_list_id) FK
            // so children carry the tenant anchor per G6-2 convention (mirroring IntakeDbContext pattern).
            // Re-homed unchanged from the former ShoppingDbContext's InitialShoppingSchema migration.
            migrationBuilder.Sql(@"
                ALTER TABLE shopping.shopping_list_item
                    DROP CONSTRAINT ""FK_shopping_list_item_shopping_list_shopping_list_id"";

                ALTER TABLE shopping.shopping_list_item
                    ADD CONSTRAINT fk_shopping_list_item_shopping_list
                    FOREIGN KEY (household_id, shopping_list_id)
                    REFERENCES shopping.shopping_list (household_id, shopping_list_id)
                    ON DELETE CASCADE;
            ");

            // ── Domain CHECK constraints (single-row invariants) ────────────────────
            // Re-homed unchanged from the former MealPlanningDbContext/ShoppingDbContext initial migrations.
            migrationBuilder.Sql(@"
                -- source must be 'manual' or 'ai'
                ALTER TABLE meal_planning.planned_meal
                    ADD CONSTRAINT ck_planned_meal_source
                    CHECK (source IN ('manual', 'ai'));

                -- stance must be one of the four valid values (M6)
                ALTER TABLE meal_planning.tag_stance
                    ADD CONSTRAINT ck_tag_stance_value
                    CHECK (stance IN ('Required', 'Preferred', 'Disliked', 'Restricted'));

                -- Item shape constraint: exactly one of product_id / free_text must be non-null
                -- (shopping.md, resolved call 3).
                ALTER TABLE shopping.shopping_list_item
                    ADD CONSTRAINT ck_shopping_list_item_product_or_free_text
                    CHECK (num_nonnulls(product_id, free_text) = 1);

                -- Source provenance: closed set CHECK + default 'manual' (shopping.md §source column).
                -- Final shape (post plantry-9scq's AddContributionModel) — the column has always carried
                -- this default on a fresh database; a fresh database never sees the intermediate
                -- pre-contribution-model version.
                ALTER TABLE shopping.shopping_list_item_contribution
                    ADD CONSTRAINT ck_contribution_source
                    CHECK (source IN ('manual', 'recipe', 'meal_plan', 'deal'));

                ALTER TABLE shopping.shopping_list_item_contribution
                    ALTER COLUMN source SET DEFAULT 'manual';
            ");

            // ── Per-household Row Level Security (ADR-008 / DM-1) ───────────────────
            // Re-homed from the two former contexts' initial migrations.
            migrationBuilder.Sql(@"
                ALTER TABLE meal_planning.meal_plan ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.meal_plan FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.meal_plan
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.planned_meal ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.planned_meal FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.planned_meal
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.planned_dish ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.planned_dish FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.planned_dish
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.meal_slot_config ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.meal_slot_config FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.meal_slot_config
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.meal_slot ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.meal_slot FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.meal_slot
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.user_preference ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.user_preference FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.user_preference
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.tag_stance ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.tag_stance FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.tag_stance
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.household_planning_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.household_planning_settings FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.household_planning_settings
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.week_planning_override ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.week_planning_override FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.week_planning_override
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA meal_planning TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA meal_planning TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA meal_planning TO app_user;

                ALTER TABLE shopping.shopping_list ENABLE ROW LEVEL SECURITY;
                ALTER TABLE shopping.shopping_list FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON shopping.shopping_list
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE shopping.shopping_list_item ENABLE ROW LEVEL SECURITY;
                ALTER TABLE shopping.shopping_list_item FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON shopping.shopping_list_item
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                -- shopping_list_item_contribution has no household_id of its own — scope through its
                -- parent shopping_list_item (mirrors the AddContributionModel migration's original policy).
                ALTER TABLE shopping.shopping_list_item_contribution ENABLE ROW LEVEL SECURITY;
                ALTER TABLE shopping.shopping_list_item_contribution FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON shopping.shopping_list_item_contribution
                  USING (
                    shopping_list_item_id IN (
                        SELECT shopping_list_item_id FROM shopping.shopping_list_item
                        WHERE household_id = NULLIF(current_setting('app.household_id', true), '')::uuid
                    )
                  );

                GRANT USAGE ON SCHEMA shopping TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA shopping TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA shopping TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA shopping FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA shopping FROM app_user;
                REVOKE USAGE ON SCHEMA shopping FROM app_user;

                DROP POLICY IF EXISTS household_isolation ON shopping.shopping_list;
                DROP POLICY IF EXISTS household_isolation ON shopping.shopping_list_item;
                DROP POLICY IF EXISTS household_isolation ON shopping.shopping_list_item_contribution;

                REVOKE ALL ON ALL TABLES IN SCHEMA meal_planning FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA meal_planning FROM app_user;
                REVOKE USAGE ON SCHEMA meal_planning FROM app_user;

                DROP POLICY IF EXISTS household_isolation ON meal_planning.meal_plan;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.planned_meal;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.planned_dish;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.meal_slot_config;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.meal_slot;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.user_preference;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.tag_stance;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.household_planning_settings;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.week_planning_override;

                ALTER TABLE meal_planning.meal_slot DROP CONSTRAINT IF EXISTS fk_meal_slot_config_composite;
                ALTER TABLE meal_planning.planned_meal DROP CONSTRAINT IF EXISTS fk_planned_meal_plan_composite;
                ALTER TABLE meal_planning.planned_meal DROP CONSTRAINT IF EXISTS fk_planned_meal_slot_composite;
                ALTER TABLE meal_planning.planned_dish DROP CONSTRAINT IF EXISTS fk_planned_dish_meal_composite;
                ALTER TABLE meal_planning.tag_stance DROP CONSTRAINT IF EXISTS fk_tag_stance_preference_composite;
            ");

            migrationBuilder.DropTable(
                name: "shopping_list_item_contribution",
                schema: "shopping");

            migrationBuilder.DropTable(
                name: "planned_dish",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "tag_stance",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "shopping_list_item",
                schema: "shopping");

            migrationBuilder.DropTable(
                name: "planned_meal",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "meal_slot",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "week_planning_override",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "user_preference",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "shopping_list",
                schema: "shopping");

            migrationBuilder.DropTable(
                name: "meal_slot_config",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "meal_plan",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "household_planning_settings",
                schema: "meal_planning");
        }
    }
}
