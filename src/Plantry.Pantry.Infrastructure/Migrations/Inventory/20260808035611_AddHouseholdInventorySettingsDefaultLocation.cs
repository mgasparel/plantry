using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pantry.Infrastructure.Migrations.Inventory
{
    /// <inheritdoc />
    public partial class AddHouseholdInventorySettingsDefaultLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_location_id",
                schema: "inventory",
                table: "household_inventory_settings",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_location_id",
                schema: "inventory",
                table: "household_inventory_settings");
        }
    }
}
