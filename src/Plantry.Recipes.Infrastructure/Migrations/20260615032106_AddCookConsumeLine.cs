using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCookConsumeLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cook_consume_line",
                schema: "recipes",
                columns: table => new
                {
                    cook_consume_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cook_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingredient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    shortfall = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cook_consume_line", x => x.cook_consume_line_id);
                    // EF-generated simple FK replaced by a composite tenant-safe FK added via raw SQL below.
                });

            migrationBuilder.CreateIndex(
                name: "IX_cook_consume_line_cook_event_id",
                schema: "recipes",
                table: "cook_consume_line",
                column: "cook_event_id");

            migrationBuilder.CreateIndex(
                name: "ix_cook_consume_line_household_event_status",
                schema: "recipes",
                table: "cook_consume_line",
                columns: new[] { "household_id", "cook_event_id", "status" });

            // Tenant-safe composite FK: add UNIQUE anchor on cook_event, then reference it from the
            // child via (household_id, cook_event_id) — consistent with the same pattern used for
            // recipe_ingredient, recipe_photo, and recipe_tag in InitialRecipesSchema (Gate 7).
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.cook_event
                    ADD CONSTRAINT uq_cook_event_household_event UNIQUE (household_id, cook_event_id);

                ALTER TABLE recipes.cook_consume_line
                    ADD CONSTRAINT fk_cook_consume_line_cook_event
                    FOREIGN KEY (household_id, cook_event_id)
                    REFERENCES recipes.cook_event (household_id, cook_event_id)
                    ON DELETE CASCADE;
            ");

            // Domain CHECK constraint for the status enum (Gate 7 / conventions.md).
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.cook_consume_line
                    ADD CONSTRAINT ck_cook_consume_line_status
                    CHECK (status IN ('Pending', 'Applied', 'Shorted'));
            ");

            // Per-household row-level security (ADR-008 / DM-1), consistent with all other
            // recipes tables in InitialRecipesSchema.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.cook_consume_line ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.cook_consume_line FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.cook_consume_line
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON recipes.cook_consume_line TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revoke grants and drop the RLS policy first (table must still exist).
            migrationBuilder.Sql(@"
                REVOKE ALL ON recipes.cook_consume_line FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON recipes.cook_consume_line;
            ");

            // Drop the child table (and its FK fk_cook_consume_line_cook_event) before removing
            // the unique constraint uq_cook_event_household_event it depends on. Postgres will
            // error if the FK referencing a constraint is present when the constraint is dropped.
            migrationBuilder.DropTable(
                name: "cook_consume_line",
                schema: "recipes");

            // Now safe to drop the parent's unique anchor (no FK depends on it any more).
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.cook_event
                    DROP CONSTRAINT IF EXISTS uq_cook_event_household_event;
            ");
        }
    }
}
