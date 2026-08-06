using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pantry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductIsProduced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_produced",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_produced",
                schema: "catalog",
                table: "products");
        }
    }
}
