using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeYieldAndCookProduceLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "yield_product_id",
                schema: "recipes",
                table: "recipe",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "yield_quantity",
                schema: "recipes",
                table: "recipe",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "yield_unit_id",
                schema: "recipes",
                table: "recipe",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cook_produce_line",
                schema: "recipes",
                columns: table => new
                {
                    cook_produce_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cook_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cook_produce_line", x => x.cook_produce_line_id);
                    // EF-generated simple FK replaced by a composite tenant-safe FK added via raw SQL below
                    // (mirrors cook_consume_line / AddCookConsumeLine, Gate 7).
                });

            migrationBuilder.CreateIndex(
                name: "IX_cook_produce_line_cook_event_id",
                schema: "recipes",
                table: "cook_produce_line",
                column: "cook_event_id");

            migrationBuilder.CreateIndex(
                name: "ix_cook_produce_line_household_event_status",
                schema: "recipes",
                table: "cook_produce_line",
                columns: new[] { "household_id", "cook_event_id", "status" });

            // Tenant-safe composite FK referencing cook_event's (household_id, cook_event_id) unique anchor
            // (uq_cook_event_household_event already exists from AddCookConsumeLine), mirroring the
            // cook_consume_line pattern in AddCookConsumeLine (Gate 7).
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.cook_produce_line
                    ADD CONSTRAINT fk_cook_produce_line_cook_event
                    FOREIGN KEY (household_id, cook_event_id)
                    REFERENCES recipes.cook_event (household_id, cook_event_id)
                    ON DELETE CASCADE;
            ");

            // Domain CHECK constraint for the status enum (Gate 7 / conventions.md).
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.cook_produce_line
                    ADD CONSTRAINT ck_cook_produce_line_status
                    CHECK (status IN ('Pending', 'Applied', 'Failed'));
            ");

            // Per-household row-level security (ADR-008 / DM-1), consistent with all other recipes tables.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.cook_produce_line ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.cook_produce_line FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.cook_produce_line
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON recipes.cook_produce_line TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revoke grants and drop the RLS policy first (table must still exist). The FK
            // fk_cook_produce_line_cook_event drops with the table; uq_cook_event_household_event is left
            // in place because cook_consume_line still depends on it.
            migrationBuilder.Sql(@"
                REVOKE ALL ON recipes.cook_produce_line FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON recipes.cook_produce_line;
            ");

            migrationBuilder.DropTable(
                name: "cook_produce_line",
                schema: "recipes");

            migrationBuilder.DropColumn(
                name: "yield_product_id",
                schema: "recipes",
                table: "recipe");

            migrationBuilder.DropColumn(
                name: "yield_quantity",
                schema: "recipes",
                table: "recipe");

            migrationBuilder.DropColumn(
                name: "yield_unit_id",
                schema: "recipes",
                table: "recipe");
        }
    }
}
