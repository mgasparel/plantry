using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdInventorySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "household_inventory_settings",
                schema: "inventory",
                columns: table => new
                {
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiring_soon_days = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_household_inventory_settings", x => x.household_id);
                });

            // ── Per-household Row Level Security (ADR-008 / DM-1) ───────────────
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.household_inventory_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.household_inventory_settings FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.household_inventory_settings
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON inventory.household_inventory_settings TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP POLICY IF EXISTS household_isolation ON inventory.household_inventory_settings;
                REVOKE ALL ON inventory.household_inventory_settings FROM app_user;
            ");

            migrationBuilder.DropTable(
                name: "household_inventory_settings",
                schema: "inventory");
        }
    }
}
