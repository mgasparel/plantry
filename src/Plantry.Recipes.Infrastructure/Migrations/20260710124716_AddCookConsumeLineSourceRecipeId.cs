using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCookConsumeLineSourceRecipeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Cook-history provenance (recipe-composition.md D8): the sub-recipe a consume line was pulled
            // in from via an inclusion. Nullable bare soft-ref (DM-3, no FK) — null for direct lines and
            // ad-hoc added products; existing rows back-fill to null.
            migrationBuilder.AddColumn<Guid>(
                name: "source_recipe_id",
                schema: "recipes",
                table: "cook_consume_line",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_recipe_id",
                schema: "recipes",
                table: "cook_consume_line");
        }
    }
}
