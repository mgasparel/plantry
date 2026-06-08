using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CatalogReferenceUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_units_household_id_symbol",
                schema: "catalog",
                table: "units",
                columns: new[] { "household_id", "symbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_locations_household_id_name",
                schema: "catalog",
                table: "locations",
                columns: new[] { "household_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_categories_household_id_name",
                schema: "catalog",
                table: "categories",
                columns: new[] { "household_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_units_household_id_symbol",
                schema: "catalog",
                table: "units");

            migrationBuilder.DropIndex(
                name: "IX_locations_household_id_name",
                schema: "catalog",
                table: "locations");

            migrationBuilder.DropIndex(
                name: "IX_categories_household_id_name",
                schema: "catalog",
                table: "categories");
        }
    }
}
