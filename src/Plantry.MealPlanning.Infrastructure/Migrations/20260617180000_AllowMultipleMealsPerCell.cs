using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.MealPlanning.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleMealsPerCell : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add ordinal column with a DEFAULT so existing rows get 1.
            //    Existing rows are at most 1 per cell (the old unique constraint
            //    enforced that), so backfilling all of them with ordinal=1 is correct.
            migrationBuilder.Sql(
                "ALTER TABLE meal_planning.planned_meal " +
                "ADD COLUMN ordinal integer NOT NULL DEFAULT 1;");

            // 2. Remove the DEFAULT now that backfill is done.
            //    New rows must supply an explicit ordinal.
            migrationBuilder.Sql(
                "ALTER TABLE meal_planning.planned_meal " +
                "ALTER COLUMN ordinal DROP DEFAULT;");

            // 3. Drop the deferrable 3-column unique constraint introduced by
            //    MakePlannedMealSlotConstraintDeferrable. That migration replaced the
            //    original EF-managed index with a raw CONSTRAINT; both must be dropped
            //    cleanly. The swap path that required deferability is removed in this
            //    change (MP-O8: relocate-only, no swap).
            migrationBuilder.Sql(
                "ALTER TABLE meal_planning.planned_meal " +
                "DROP CONSTRAINT IF EXISTS ux_planned_meal_plan_date_slot;");

            // Also drop the original EF-managed index in case the deferrable migration
            // was never applied (e.g. fresh test databases).
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS meal_planning.ux_planned_meal_plan_date_slot;");

            // 4. Create the new 4-column unique index: one row per (plan, date, slot, ordinal).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_planned_meal_plan_date_slot_ordinal " +
                "ON meal_planning.planned_meal (meal_plan_id, date, meal_slot_id, ordinal);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the 4-column index
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS meal_planning.ux_planned_meal_plan_date_slot_ordinal;");

            // Restore the deferrable 3-column unique constraint
            migrationBuilder.Sql(
                "ALTER TABLE meal_planning.planned_meal " +
                "ADD CONSTRAINT ux_planned_meal_plan_date_slot " +
                "UNIQUE (meal_plan_id, date, meal_slot_id) " +
                "DEFERRABLE INITIALLY DEFERRED;");

            // Remove the ordinal column
            migrationBuilder.Sql(
                "ALTER TABLE meal_planning.planned_meal " +
                "DROP COLUMN IF EXISTS ordinal;");
        }
    }
}
