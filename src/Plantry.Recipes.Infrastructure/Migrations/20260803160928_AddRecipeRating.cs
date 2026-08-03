using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recipe_rating",
                schema: "recipes",
                columns: table => new
                {
                    recipe_rating_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stars = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_rating", x => x.recipe_rating_id);
                    // No EF-generated simple FK — the tenant-safe composite FK to recipe is added via
                    // raw SQL below, mirroring recipe_inclusion / recipe_ingredient / recipe_photo.
                });

            migrationBuilder.CreateIndex(
                name: "ux_recipe_rating_household_recipe_user",
                schema: "recipes",
                table: "recipe_rating",
                columns: new[] { "household_id", "recipe_id", "user_id" },
                unique: true);

            // Tenant-safe composite FK: references the uq_recipe_household_recipe anchor on recipes.recipe
            // via (household_id, recipe_id), consistent with recipe_ingredient / recipe_photo /
            // recipe_inclusion in prior migrations (Gate 3 / Gate 7). ON DELETE RESTRICT — a recipe is
            // soft-deleted (archived_at), never physically removed, so ratings on archived recipes persist
            // (plantry-zlwp epic — "Ratings on archived recipes persist") and RESTRICT never actually fires.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe_rating
                    ADD CONSTRAINT fk_recipe_rating_recipe
                    FOREIGN KEY (household_id, recipe_id)
                    REFERENCES recipes.recipe (household_id, recipe_id)
                    ON DELETE RESTRICT;
            ");

            // Domain invariant the DB can enforce directly: Stars in 1..5 (whole), as a backstop to the
            // aggregate's own ValidateStars check (Gate 7 / conventions.md).
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe_rating
                    ADD CONSTRAINT ck_recipe_rating_stars CHECK (stars BETWEEN 1 AND 5);
            ");

            // Per-household row-level security (ADR-008 / DM-1), consistent with all other recipes tables.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe_rating ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.recipe_rating FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.recipe_rating
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON recipes.recipe_rating TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revoke grants and drop the RLS policy first (table must still exist).
            migrationBuilder.Sql(@"
                REVOKE ALL ON recipes.recipe_rating FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON recipes.recipe_rating;
            ");

            migrationBuilder.DropTable(
                name: "recipe_rating",
                schema: "recipes");
        }
    }
}
