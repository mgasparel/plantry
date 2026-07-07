using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Deals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FlyerImportDedupParsedOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_flyer_import_household_store_external",
                schema: "deals",
                table: "flyer_import");

            migrationBuilder.CreateIndex(
                name: "ux_flyer_import_household_store_external",
                schema: "deals",
                table: "flyer_import",
                columns: new[] { "household_id", "store_id", "flyer_external_id" },
                unique: true,
                filter: "status = 'parsed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_flyer_import_household_store_external",
                schema: "deals",
                table: "flyer_import");

            migrationBuilder.CreateIndex(
                name: "ux_flyer_import_household_store_external",
                schema: "deals",
                table: "flyer_import",
                columns: new[] { "household_id", "store_id", "flyer_external_id" },
                unique: true);
        }
    }
}
