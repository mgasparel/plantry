using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitDisplayStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Display style (quantity-display.md Q2). Existing rows default to 'decimal' — the
            // historical rendering — so the column default preserves current behaviour on the ALTER.
            // EF inserts always send an explicit value.
            migrationBuilder.AddColumn<string>(
                name: "display_style",
                schema: "catalog",
                table: "units",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "decimal");

            // Enums persist as text + CHECK (Gate 7 convention), never a Postgres ENUM —
            // mirrors ck_product_conversions_source.
            migrationBuilder.AddCheckConstraint(
                name: "ck_units_display_style",
                schema: "catalog",
                table: "units",
                sql: "display_style IN ('decimal','fraction')");

            // One-time data migration (quantity-display.md Q10): opt the seeded scoop-measured
            // volume units into fraction display for households created before this feature.
            // Matched case-insensitively by unit code (stored in the `symbol` column). Custom units
            // and every other unit keep the 'decimal' default set above.
            migrationBuilder.Sql(
                "UPDATE catalog.units SET display_style = 'fraction' " +
                "WHERE lower(symbol) IN ('cup', 'tbsp', 'tsp');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_units_display_style",
                schema: "catalog",
                table: "units");

            migrationBuilder.DropColumn(
                name: "display_style",
                schema: "catalog",
                table: "units");
        }
    }
}
