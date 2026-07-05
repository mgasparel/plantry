using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeferredUnitGapCookLineStatuses : Migration
    {
        // Widens the cook_consume_line.status domain CHECK to admit the two plantry-qll2.6 lifecycle
        // states — DeferredUnitGap (a consume owed until a conversion bridges the unit gap) and
        // SupersededByCount (a deferred line voided by an absolute Take Stock observation). Enums persist
        // as text + CHECK (Gate 7 / conventions.md), so this is a constraint swap, not a column change —
        // the EF model is unchanged (status is mapped via HasConversion), so there is no snapshot diff.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.cook_consume_line
                    DROP CONSTRAINT ck_cook_consume_line_status;

                ALTER TABLE recipes.cook_consume_line
                    ADD CONSTRAINT ck_cook_consume_line_status
                    CHECK (status IN ('Pending', 'Applied', 'Shorted', 'DeferredUnitGap', 'SupersededByCount'));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert to the original three-value domain. Any rows already carrying the new states would
            // violate the narrower constraint; roll them back to their nearest pre-qll2.6 meaning first
            // (both represent an un-applied consume that never wrote a journal row → Shorted).
            migrationBuilder.Sql(@"
                UPDATE recipes.cook_consume_line
                    SET status = 'Shorted'
                    WHERE status IN ('DeferredUnitGap', 'SupersededByCount');

                ALTER TABLE recipes.cook_consume_line
                    DROP CONSTRAINT ck_cook_consume_line_status;

                ALTER TABLE recipes.cook_consume_line
                    ADD CONSTRAINT ck_cook_consume_line_status
                    CHECK (status IN ('Pending', 'Applied', 'Shorted'));
            ");
        }
    }
}
