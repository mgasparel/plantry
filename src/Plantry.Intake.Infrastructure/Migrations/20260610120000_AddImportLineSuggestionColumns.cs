using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImportLineSuggestionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "suggested_price",
                schema: "intake",
                table: "import_line",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "suggested_product_id",
                schema: "intake",
                table: "import_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suggested_product_name",
                schema: "intake",
                table: "import_line",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "suggested_quantity",
                schema: "intake",
                table: "import_line",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suggested_unit_label",
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
                name: "suggested_price",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "suggested_product_id",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "suggested_product_name",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "suggested_quantity",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "suggested_unit_label",
                schema: "intake",
                table: "import_line");
        }
    }
}
