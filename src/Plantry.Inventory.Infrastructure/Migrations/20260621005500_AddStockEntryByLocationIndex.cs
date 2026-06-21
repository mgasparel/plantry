using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockEntryByLocationIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_stock_entry_by_location",
                schema: "inventory",
                table: "stock_entry",
                columns: new[] { "household_id", "location_id", "product_id" },
                filter: "depleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stock_entry_by_location",
                schema: "inventory",
                table: "stock_entry");
        }
    }
}
