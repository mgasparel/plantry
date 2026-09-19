using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pantry.Infrastructure.Migrations.Pantry
{
    /// <summary>
    /// Introduces the Inventory-context <c>low_stock_rule</c> table (plantry-oh27.1) and retires
    /// <c>product_stock.low_stock_threshold</c> — after this migration there is exactly one threshold
    /// mechanism, keyed by product id alone (leaf OR parent) rather than by the leaf-only
    /// <c>product_stock</c> row. Data moves in three steps so no household's existing threshold is
    /// lost: create the table, copy every non-null positive threshold across, then drop the old column.
    /// </summary>
    public partial class AddLowStockRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "low_stock_rule",
                schema: "inventory",
                columns: table => new
                {
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    threshold = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_low_stock_rule", x => new { x.household_id, x.product_id });
                });

            // Data move: null/zero on product_stock always meant "no threshold" (never persisted as a
            // row here), matching LowStockRule's own "absence of a row = no threshold" invariant.
            migrationBuilder.Sql(@"
                INSERT INTO inventory.low_stock_rule (household_id, product_id, threshold, updated_at)
                SELECT household_id, product_id, low_stock_threshold, updated_at
                FROM inventory.product_stock
                WHERE low_stock_threshold IS NOT NULL AND low_stock_threshold > 0;
            ");

            migrationBuilder.DropColumn(
                name: "low_stock_threshold",
                schema: "inventory",
                table: "product_stock");

            // RLS backstop (ADR-008), mirroring every other inventory.* table exactly (household_isolation
            // policy + the same NULLIF-empty-tenant guard) plus the explicit per-table grant a brand-new
            // table needs — the schema-wide "GRANT ... ON ALL TABLES IN SCHEMA inventory" from
            // InitialPantrySchema only covered the tables that existed when it ran.
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.low_stock_rule ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.low_stock_rule FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.low_stock_rule
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON inventory.low_stock_rule TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON inventory.low_stock_rule FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON inventory.low_stock_rule;
            ");

            migrationBuilder.AddColumn<decimal>(
                name: "low_stock_threshold",
                schema: "inventory",
                table: "product_stock",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            // Data move back: only rules for products that actually hold a product_stock row can be
            // represented by the reinstated column — a rule for a parent product (never a ProductStock
            // owner) has nowhere to go on rollback and is intentionally dropped, matching the rule's own
            // pre-plantry-oh27.1 constraint that a threshold could only ever be set on a leaf.
            migrationBuilder.Sql(@"
                UPDATE inventory.product_stock ps
                SET low_stock_threshold = r.threshold
                FROM inventory.low_stock_rule r
                WHERE ps.household_id = r.household_id AND ps.product_id = r.product_id;
            ");

            migrationBuilder.DropTable(
                name: "low_stock_rule",
                schema: "inventory");
        }
    }
}
