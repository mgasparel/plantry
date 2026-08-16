using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductDefaultLocationToIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "new_product_default_unit_id",
                schema: "intake",
                table: "import_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "new_product_default_location_id",
                schema: "intake",
                table: "import_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "default_location_id",
                schema: "intake",
                table: "staged_product",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "category_id",
                schema: "intake",
                table: "staged_product",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "category_id",
                schema: "intake",
                table: "staged_product",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
            migrationBuilder.DropColumn(name: "new_product_default_unit_id", schema: "intake", table: "import_line");
            migrationBuilder.DropColumn(name: "new_product_default_location_id", schema: "intake", table: "import_line");
            migrationBuilder.DropColumn(name: "default_location_id", schema: "intake", table: "staged_product");
        }
    }
}
