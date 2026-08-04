using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pantry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductNeverExpiryPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "never_expires_after_freezing",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "never_expires_after_thawing",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "never_expires_after_freezing",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "never_expires_after_thawing",
                schema: "catalog",
                table: "products");
        }
    }
}
