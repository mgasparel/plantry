using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.MealPlanning.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialMealPlanningSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "meal_planning");

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
                name: "planned_meal",
                schema: "meal_planning",
                columns: table => new
                {
                    planned_meal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meal_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    meal_slot_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    servings = table.Column<int>(type: "integer", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planned_dish", x => x.planned_dish_id);
                    table.ForeignKey(
                        name: "FK_planned_dish_planned_meal_planned_meal_id",
                        column: x => x.planned_meal_id,
                        principalSchema: "meal_planning",
                        principalTable: "planned_meal",
                        principalColumn: "planned_meal_id",
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
                name: "ux_planned_dish_meal_ordinal",
                schema: "meal_planning",
                table: "planned_dish",
                columns: new[] { "planned_meal_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_planned_meal_household_id",
                schema: "meal_planning",
                table: "planned_meal",
                columns: new[] { "household_id", "planned_meal_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_planned_meal_plan_date_slot",
                schema: "meal_planning",
                table: "planned_meal",
                columns: new[] { "meal_plan_id", "date", "meal_slot_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tag_stance_pref_tag",
                schema: "meal_planning",
                table: "tag_stance",
                columns: new[] { "user_preference_id", "tag_id" },
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

            // ── Tenant-safe composite FKs (ADR-008 / conventions.md) ────────────
            // EF adds simple PKs-based FKs above; we add COMPOSITE FK anchors so
            // every child row is tied to BOTH the PK and household_id, preventing
            // cross-household FK bypass.
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

            // ── Domain CHECK constraints ─────────────────────────────────────────
            migrationBuilder.Sql(@"
                -- M12: exactly one of recipe_id / product_id is set
                ALTER TABLE meal_planning.planned_dish
                    ADD CONSTRAINT ck_planned_dish_xor
                    CHECK (num_nonnulls(recipe_id, product_id) = 1);

                -- M3: servings >= 1
                ALTER TABLE meal_planning.planned_dish
                    ADD CONSTRAINT ck_planned_dish_servings
                    CHECK (servings >= 1);

                -- source must be 'manual' or 'ai'
                ALTER TABLE meal_planning.planned_meal
                    ADD CONSTRAINT ck_planned_meal_source
                    CHECK (source IN ('manual', 'ai'));

                -- stance must be one of the four valid values (M6)
                ALTER TABLE meal_planning.tag_stance
                    ADD CONSTRAINT ck_tag_stance_value
                    CHECK (stance IN ('Required', 'Preferred', 'Disliked', 'Restricted'));
            ");

            // ── Per-household Row Level Security (ADR-008 / DM-1) ───────────────
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

                GRANT USAGE ON SCHEMA meal_planning TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA meal_planning TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA meal_planning TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
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
            ");

            migrationBuilder.DropTable(
                name: "meal_slot",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "planned_dish",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "tag_stance",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "meal_slot_config",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "planned_meal",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "user_preference",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "meal_plan",
                schema: "meal_planning");
        }
    }
}
