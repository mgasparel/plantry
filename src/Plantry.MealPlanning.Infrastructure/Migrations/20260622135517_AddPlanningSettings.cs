using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.MealPlanning.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            // ── Per-household Row Level Security (ADR-008 / DM-1) ───────────────
            migrationBuilder.Sql(@"
                ALTER TABLE meal_planning.household_planning_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.household_planning_settings FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.household_planning_settings
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE meal_planning.week_planning_override ENABLE ROW LEVEL SECURITY;
                ALTER TABLE meal_planning.week_planning_override FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON meal_planning.week_planning_override
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON meal_planning.household_planning_settings TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON meal_planning.week_planning_override TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP POLICY IF EXISTS household_isolation ON meal_planning.household_planning_settings;
                DROP POLICY IF EXISTS household_isolation ON meal_planning.week_planning_override;
                REVOKE ALL ON meal_planning.household_planning_settings FROM app_user;
                REVOKE ALL ON meal_planning.week_planning_override FROM app_user;
            ");

            migrationBuilder.DropTable(
                name: "household_planning_settings",
                schema: "meal_planning");

            migrationBuilder.DropTable(
                name: "week_planning_override",
                schema: "meal_planning");
        }
    }
}
