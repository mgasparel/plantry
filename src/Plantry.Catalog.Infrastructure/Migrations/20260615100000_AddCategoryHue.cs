using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryHue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "hue",
                schema: "catalog",
                table: "categories",
                type: "integer",
                nullable: true);

            // Backfill sensible default hues for known category names seeded by CatalogReferenceDataSeeder.
            // New categories created after this migration default to null (neutral chip) until the user assigns.
            migrationBuilder.Sql("""
                UPDATE catalog.categories SET hue = CASE
                    WHEN name = 'Dairy & Eggs'             THEN 210
                    WHEN name = 'Meat & Fish'              THEN 10
                    WHEN name = 'Fruits and Vegetables'    THEN 145
                    WHEN name = 'Bread & Bakery'           THEN 30
                    WHEN name = 'Deli'                     THEN 45
                    WHEN name = 'Frozen'                   THEN 200
                    WHEN name = 'Pantry Staples'           THEN 60
                    WHEN name = 'Canned & Jarred'          THEN 25
                    WHEN name = 'Drinks'                   THEN 240
                    WHEN name = 'Condiments'               THEN 80
                    WHEN name = 'Herbs and Spices'         THEN 120
                    WHEN name = 'Snacks'                   THEN 350
                    WHEN name = 'Other'                    THEN 270
                    ELSE NULL
                END
                WHERE hue IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hue",
                schema: "catalog",
                table: "categories");
        }
    }
}
