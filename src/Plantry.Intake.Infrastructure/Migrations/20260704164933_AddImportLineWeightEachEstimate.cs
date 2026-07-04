using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImportLineWeightEachEstimate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "estimated_each_confidence",
                schema: "intake",
                table: "import_line",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "estimated_each_count",
                schema: "intake",
                table: "import_line",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "receipt_weight",
                schema: "intake",
                table: "import_line",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_weight_unit_label",
                schema: "intake",
                table: "import_line",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "estimated_each_confidence",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "estimated_each_count",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "receipt_weight",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "receipt_weight_unit_label",
                schema: "intake",
                table: "import_line");
        }
    }
}
