using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCookEventPlannedDishId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "planned_dish_id",
                schema: "recipes",
                table: "cook_event",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_cook_event_household_planned_dish",
                schema: "recipes",
                table: "cook_event",
                columns: new[] { "household_id", "planned_dish_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cook_event_household_planned_dish",
                schema: "recipes",
                table: "cook_event");

            migrationBuilder.DropColumn(
                name: "planned_dish_id",
                schema: "recipes",
                table: "cook_event");
        }
    }
}
