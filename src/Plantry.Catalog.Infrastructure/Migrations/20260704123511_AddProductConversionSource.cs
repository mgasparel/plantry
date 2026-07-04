using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductConversionSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Provenance of a product conversion's factor (ADR-022). Existing rows were all
            // implicitly user-authoritative, so backfill them to 'user_confirmed'; the column
            // default exists solely for this ALTER. EF inserts always send an explicit value.
            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "catalog",
                table: "product_conversions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "user_confirmed");

            // Enums persist as text + CHECK (Gate 7 convention), never a Postgres ENUM.
            migrationBuilder.AddCheckConstraint(
                name: "ck_product_conversions_source",
                schema: "catalog",
                table: "product_conversions",
                sql: "source IN ('user_confirmed','ai_suggested')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_product_conversions_source",
                schema: "catalog",
                table: "product_conversions");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "catalog",
                table: "product_conversions");
        }
    }
}
