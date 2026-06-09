using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "product_stock",
                schema: "inventory",
                columns: table => new
                {
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_stock", x => new { x.household_id, x.product_id });
                });

            migrationBuilder.CreateTable(
                name: "stock_entry",
                schema: "inventory",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    frozen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    thawed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purchased_at = table.Column<DateOnly>(type: "date", nullable: true),
                    depleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_entry", x => x.entry_id);
                    table.ForeignKey(
                        name: "FK_stock_entry_product_stock_household_id_product_id",
                        columns: x => new { x.household_id, x.product_id },
                        principalSchema: "inventory",
                        principalTable: "product_stock",
                        principalColumns: new[] { "household_id", "product_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_journal_entry",
                schema: "inventory",
                columns: table => new
                {
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delta = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    source_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_journal_entry", x => x.journal_id);
                    table.ForeignKey(
                        name: "FK_stock_journal_entry_product_stock_household_id_product_id",
                        columns: x => new { x.household_id, x.product_id },
                        principalSchema: "inventory",
                        principalTable: "product_stock",
                        principalColumns: new[] { "household_id", "product_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stock_journal_entry_stock_entry_entry_id",
                        column: x => x.entry_id,
                        principalSchema: "inventory",
                        principalTable: "stock_entry",
                        principalColumn: "entry_id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_entry_fefo",
                schema: "inventory",
                table: "stock_entry",
                columns: new[] { "household_id", "product_id", "expiry_date", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_entry_household_id",
                schema: "inventory",
                table: "stock_entry",
                column: "household_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_journal_entry_entry_id",
                schema: "inventory",
                table: "stock_journal_entry",
                column: "entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_journal_entry_household_id",
                schema: "inventory",
                table: "stock_journal_entry",
                column: "household_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_journal_entry_household_id_product_id",
                schema: "inventory",
                table: "stock_journal_entry",
                columns: new[] { "household_id", "product_id" });

            // The journal's why-taxonomy is a closed set (ADR-011 / DM-14) — enforce it in the DB.
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_reason
                    CHECK (reason IN ('Purchase','Consumed','Discarded','Correction'));
            ");

            // RLS — same backstop as the catalog/identity schemas: even if the app-layer query
            // filter is bypassed, Postgres enforces household isolation for every inventory table.
            // The non-superuser app_user role already exists (created by InitialCatalogSchema).
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.product_stock ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.product_stock FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.product_stock
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE inventory.stock_entry ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.stock_entry FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.stock_entry
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE inventory.stock_journal_entry ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.stock_journal_entry FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.stock_journal_entry
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA inventory TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inventory TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA inventory TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA inventory FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA inventory FROM app_user;
                REVOKE USAGE ON SCHEMA inventory FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON inventory.product_stock;
                DROP POLICY IF EXISTS household_isolation ON inventory.stock_entry;
                DROP POLICY IF EXISTS household_isolation ON inventory.stock_journal_entry;
            ");

            migrationBuilder.DropTable(
                name: "stock_journal_entry",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_entry",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "product_stock",
                schema: "inventory");
        }
    }
}
