using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdDisplayCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "display_currency",
                schema: "identity",
                table: "households",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "display_currency",
                schema: "identity",
                table: "households");
        }
    }
}
