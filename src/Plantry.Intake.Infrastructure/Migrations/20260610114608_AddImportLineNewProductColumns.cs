using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImportLineNewProductColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "new_product_category_id",
                schema: "intake",
                table: "import_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "new_product_name",
                schema: "intake",
                table: "import_line",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "new_product_category_id",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "new_product_name",
                schema: "intake",
                table: "import_line");
        }
    }
}
