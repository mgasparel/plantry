using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropRedundantStockEntryHouseholdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stock_entry_household_id",
                schema: "inventory",
                table: "stock_entry");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_stock_entry_household_id",
                schema: "inventory",
                table: "stock_entry",
                column: "household_id");
        }
    }
}
