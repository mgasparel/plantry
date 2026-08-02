using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStagedProductNormalizedName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalized_name",
                schema: "intake",
                table: "staged_product",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Staged products were introduced immediately before this key. Backfill existing rows with
            // the same trim/collapse/invariant-case canonical form used by StagedProduct.Create before
            // making the column required and enforcing household/session uniqueness.
            migrationBuilder.Sql("""
                UPDATE intake.staged_product
                SET normalized_name = upper(regexp_replace(btrim(name), '\s+', ' ', 'g'));
                """);

            migrationBuilder.AlterColumn<string>(
                name: "normalized_name",
                schema: "intake",
                table: "staged_product",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "uq_staged_product_household_session_normalized_name",
                schema: "intake",
                table: "staged_product",
                columns: new[] { "household_id", "session_id", "normalized_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_staged_product_household_session_normalized_name",
                schema: "intake",
                table: "staged_product");

            migrationBuilder.DropColumn(
                name: "normalized_name",
                schema: "intake",
                table: "staged_product");
        }
    }
}
