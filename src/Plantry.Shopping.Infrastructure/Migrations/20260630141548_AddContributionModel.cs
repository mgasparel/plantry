using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Shopping.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContributionModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Create the new contribution child table BEFORE dropping old columns,
            // so the backfill SQL (step 2) can read source/source_ref/quantity from the item row.
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
                name: "ix_shopping_list_item_contribution_item",
                schema: "shopping",
                table: "shopping_list_item_contribution",
                column: "shopping_list_item_id");

            // Source provenance constraint and default.
            migrationBuilder.Sql(@"
                ALTER TABLE shopping.shopping_list_item_contribution
                    ADD CONSTRAINT ck_contribution_source
                    CHECK (source IN ('manual', 'recipe', 'meal_plan', 'deal'));

                ALTER TABLE shopping.shopping_list_item_contribution
                    ALTER COLUMN source SET DEFAULT 'manual';
            ");

            // Step 2: Backfill — every existing shopping_list_item row was created before the
            // per-source contribution model existed, so all rows are Manual (no Recipe producer existed yet).
            // Each row migrates to exactly ONE Manual contribution (SourceRef = null, carrying the
            // row's existing quantity and unit_id).
            migrationBuilder.Sql(@"
                INSERT INTO shopping.shopping_list_item_contribution
                    (contribution_id, shopping_list_item_id, source, source_ref, quantity, unit_id)
                SELECT
                    gen_random_uuid(),
                    shopping_list_item_id,
                    source,   -- preserves the original source value (was always 'manual' pre-9scq)
                    source_ref,
                    quantity,
                    unit_id
                FROM shopping.shopping_list_item;
            ");

            // Row-level security: mirrors the parent item table pattern (InitialShoppingSchema).
            migrationBuilder.Sql(@"
                ALTER TABLE shopping.shopping_list_item_contribution ENABLE ROW LEVEL SECURITY;
                ALTER TABLE shopping.shopping_list_item_contribution FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON shopping.shopping_list_item_contribution
                  USING (
                    shopping_list_item_id IN (
                        SELECT shopping_list_item_id FROM shopping.shopping_list_item
                        WHERE household_id = NULLIF(current_setting('app.household_id', true), '')::uuid
                    )
                  );

                GRANT SELECT, INSERT, UPDATE, DELETE ON shopping.shopping_list_item_contribution TO app_user;
            ");

            // Step 3: Drop the old columns from shopping_list_item now that contributions carry them.
            migrationBuilder.DropColumn(
                name: "quantity",
                schema: "shopping",
                table: "shopping_list_item");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "shopping",
                table: "shopping_list_item");

            migrationBuilder.DropColumn(
                name: "source_ref",
                schema: "shopping",
                table: "shopping_list_item");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the dropped columns to the item table.
            migrationBuilder.AddColumn<decimal>(
                name: "quantity",
                schema: "shopping",
                table: "shopping_list_item",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "shopping",
                table: "shopping_list_item",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<Guid>(
                name: "source_ref",
                schema: "shopping",
                table: "shopping_list_item",
                type: "uuid",
                nullable: true);

            // Restore from the first contribution per item (best-effort backfill for down migration).
            migrationBuilder.Sql(@"
                UPDATE shopping.shopping_list_item i
                SET
                    quantity  = c.quantity,
                    source    = c.source,
                    source_ref = c.source_ref,
                    unit_id   = COALESCE(i.unit_id, c.unit_id)
                FROM (
                    SELECT DISTINCT ON (shopping_list_item_id)
                        shopping_list_item_id, quantity, source, source_ref, unit_id
                    FROM shopping.shopping_list_item_contribution
                    ORDER BY shopping_list_item_id, contribution_id
                ) c
                WHERE i.shopping_list_item_id = c.shopping_list_item_id;
            ");

            migrationBuilder.Sql(@"
                DROP POLICY IF EXISTS household_isolation ON shopping.shopping_list_item_contribution;
                REVOKE ALL ON shopping.shopping_list_item_contribution FROM app_user;
            ");

            migrationBuilder.DropTable(
                name: "shopping_list_item_contribution",
                schema: "shopping");
        }
    }
}
