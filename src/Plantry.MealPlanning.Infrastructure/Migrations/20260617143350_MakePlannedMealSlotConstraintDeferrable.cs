using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.MealPlanning.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakePlannedMealSlotConstraintDeferrable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The swap operation issues two UPDATEs within one transaction: the "mover" takes the
            // target cell and the "target" takes the mover cell. PostgreSQL's default IMMEDIATE
            // unique constraint checks after every row, so the first UPDATE conflicts with the
            // still-in-place second row. Making the constraint DEFERRABLE INITIALLY DEFERRED
            // causes PostgreSQL to check uniqueness only at COMMIT, allowing both UPDATEs to
            // complete cleanly within the same transaction.
            //
            // The initial migration creates a UNIQUE INDEX (EF CreateIndex). A unique index cannot
            // be made deferrable — it must be replaced by a UNIQUE CONSTRAINT. Both back the same
            // B-tree, so query plan behaviour is identical.
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS meal_planning.ux_planned_meal_plan_date_slot;");
            migrationBuilder.Sql(
                "ALTER TABLE meal_planning.planned_meal " +
                "ADD CONSTRAINT ux_planned_meal_plan_date_slot " +
                "UNIQUE (meal_plan_id, date, meal_slot_id) " +
                "DEFERRABLE INITIALLY DEFERRED;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE meal_planning.planned_meal " +
                "DROP CONSTRAINT IF EXISTS ux_planned_meal_plan_date_slot;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_planned_meal_plan_date_slot " +
                "ON meal_planning.planned_meal (meal_plan_id, date, meal_slot_id);");
        }
    }
}
