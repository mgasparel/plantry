using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pantry.Infrastructure.Migrations.Pantry
{
    /// <inheritdoc />
    public partial class AddDefaultProducedCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_produced_category_id",
                schema: "inventory",
                table: "household_inventory_settings",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_produced_category_id",
                schema: "inventory",
                table: "household_inventory_settings");
        }
    }
}
