using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Unit system (quantity-display.md Q5, amended 2026-07-11): the explicit metric/imperial
            // firewall for simplification. Existing rows default to 'unspecified' — a unit that anchors
            // no simplification family — so the ALTER changes no behaviour until units are classified
            // below. EF inserts always send an explicit value.
            migrationBuilder.AddColumn<string>(
                name: "unit_system",
                schema: "catalog",
                table: "units",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "unspecified");

            // Enums persist as text + CHECK (Gate 7 convention), never a Postgres ENUM —
            // mirrors ck_units_display_style / ck_product_conversions_source.
            migrationBuilder.AddCheckConstraint(
                name: "ck_units_unit_system",
                schema: "catalog",
                table: "units",
                sql: "unit_system IN ('unspecified','metric','us_customary')");

            // One-time data migration: classify the standard seeded units for households created before
            // this feature. Matched case-insensitively by unit code (stored in the `symbol` column).
            // User-created units keep the 'unspecified' default until the household classifies them.
            migrationBuilder.Sql(
                "UPDATE catalog.units SET unit_system = 'metric' " +
                "WHERE lower(symbol) IN ('ml', 'l', 'g', 'kg', 'mg');");

            migrationBuilder.Sql(
                "UPDATE catalog.units SET unit_system = 'us_customary' " +
                "WHERE lower(symbol) IN ('oz', 'lb', 'fl oz', 'cup', 'tsp', 'tbsp');");

            // Re-seed the US-customary spoon/floz factors to nutrition-label values (tsp 5, tbsp 15,
            // fl oz 30; cup is already 240) so within-family ratios are exactly 3 / 2 / 8 / 16 and the
            // integer-ratio math guarantee holds against real data (quantity-display.md §6). Updated
            // ONLY where the factor still equals the original seeded value — any household that
            // hand-edited a factor keeps its value. Decimal literals only (factor_to_base is numeric;
            // no float casts or computed expressions).
            migrationBuilder.Sql(
                "UPDATE catalog.units SET factor_to_base = 5 " +
                "WHERE lower(symbol) = 'tsp' AND factor_to_base = 4.92892;");

            migrationBuilder.Sql(
                "UPDATE catalog.units SET factor_to_base = 15 " +
                "WHERE lower(symbol) = 'tbsp' AND factor_to_base = 14.7868;");

            migrationBuilder.Sql(
                "UPDATE catalog.units SET factor_to_base = 30 " +
                "WHERE lower(symbol) = 'fl oz' AND factor_to_base = 29.5735;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Factor re-seeding is data, not schema, and is intentionally not reversed — the label
            // values are the intended seed going forward. Only the column + constraint are dropped.
            migrationBuilder.DropCheckConstraint(
                name: "ck_units_unit_system",
                schema: "catalog",
                table: "units");

            migrationBuilder.DropColumn(
                name: "unit_system",
                schema: "catalog",
                table: "units");
        }
    }
}
