using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdExpiryDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "default_due_days_after_freezing",
                schema: "identity",
                table: "households",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "default_due_days_after_thawing",
                schema: "identity",
                table: "households",
                type: "integer",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_due_days_after_freezing",
                schema: "identity",
                table: "households");

            migrationBuilder.DropColumn(
                name: "default_due_days_after_thawing",
                schema: "identity",
                table: "households");
        }
    }
}
