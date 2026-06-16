using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Shopping.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddItemCategoryId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Soft ref to catalog.category — used by the recategorize action (plantry-259).
            // No FK constraint: catalog categories are reference data in a separate bounded context
            // (cross-context FK is explicitly prohibited by ADR-002 / G1-3 pattern).
            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                schema: "shopping",
                table: "shopping_list_item",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "category_id",
                schema: "shopping",
                table: "shopping_list_item");
        }
    }
}
