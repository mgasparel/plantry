using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.MealPlanning.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMealSlotIncludeInAutoPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "include_in_auto_plan",
                schema: "meal_planning",
                table: "meal_slot",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "include_in_auto_plan",
                schema: "meal_planning",
                table: "meal_slot");
        }
    }
}
