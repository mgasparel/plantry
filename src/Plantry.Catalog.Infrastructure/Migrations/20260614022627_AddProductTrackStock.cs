using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductTrackStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill existing products as tracked (the prior, only behaviour); the column default
            // exists solely for this ALTER. EF inserts always send an explicit value, so inline
            // untracked-staple creation (track_stock = false) is still honoured.
            migrationBuilder.AddColumn<bool>(
                name: "track_stock",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "track_stock",
                schema: "catalog",
                table: "products");
        }
    }
}
