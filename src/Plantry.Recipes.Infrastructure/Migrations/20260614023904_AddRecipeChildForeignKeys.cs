using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeChildForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_recipe_ingredient_recipe_recipe_id",
                schema: "recipes",
                table: "recipe_ingredient",
                column: "recipe_id",
                principalSchema: "recipes",
                principalTable: "recipe",
                principalColumn: "recipe_id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_recipe_photo_recipe_recipe_id",
                schema: "recipes",
                table: "recipe_photo",
                column: "recipe_id",
                principalSchema: "recipes",
                principalTable: "recipe",
                principalColumn: "recipe_id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_recipe_tag_recipe_recipe_id",
                schema: "recipes",
                table: "recipe_tag",
                column: "recipe_id",
                principalSchema: "recipes",
                principalTable: "recipe",
                principalColumn: "recipe_id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_ingredient_recipe_recipe_id",
                schema: "recipes",
                table: "recipe_ingredient");

            migrationBuilder.DropForeignKey(
                name: "FK_recipe_photo_recipe_recipe_id",
                schema: "recipes",
                table: "recipe_photo");

            migrationBuilder.DropForeignKey(
                name: "FK_recipe_tag_recipe_recipe_id",
                schema: "recipes",
                table: "recipe_tag");
        }
    }
}
