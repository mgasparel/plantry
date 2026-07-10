using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeInclusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recipe_inclusion",
                schema: "recipes",
                columns: table => new
                {
                    inclusion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub_recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    servings = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    group_heading = table.Column<string>(type: "text", nullable: true),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_inclusion", x => x.inclusion_id);
                    // EF-generated simple FK replaced by composite tenant-safe FKs added via raw SQL below.
                });

            migrationBuilder.CreateIndex(
                name: "ix_recipe_inclusion_household_sub",
                schema: "recipes",
                table: "recipe_inclusion",
                columns: new[] { "household_id", "sub_recipe_id" });

            migrationBuilder.CreateIndex(
                name: "ux_recipe_inclusion_recipe_ordinal",
                schema: "recipes",
                table: "recipe_inclusion",
                columns: new[] { "recipe_id", "ordinal" },
                unique: true);

            // Tenant-safe composite FKs: reference the existing uq_recipe_household_recipe anchor on
            // recipes.recipe via (household_id, recipe_id) for the owning parent, and via
            // (household_id, sub_recipe_id) for the included sub — so a child can never reference another
            // tenant's recipe. Consistent with recipe_ingredient / recipe_photo / recipe_tag in
            // InitialRecipesSchema (Gate 3 / Gate 7). Parent deletes cascade; a recipe is soft-deleted
            // (archived_at) rather than physically removed (N5/D12 also blocks archival while included),
            // so the sub RESTRICT never fires yet keeps every inclusion edge valid.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe_inclusion
                    ADD CONSTRAINT fk_recipe_inclusion_recipe
                    FOREIGN KEY (household_id, recipe_id)
                    REFERENCES recipes.recipe (household_id, recipe_id)
                    ON DELETE CASCADE;

                ALTER TABLE recipes.recipe_inclusion
                    ADD CONSTRAINT fk_recipe_inclusion_sub_recipe
                    FOREIGN KEY (household_id, sub_recipe_id)
                    REFERENCES recipes.recipe (household_id, recipe_id)
                    ON DELETE RESTRICT;
            ");

            // Domain CHECK constraints the DB can enforce directly: N1 (servings > 0) and N2 (no
            // self-inclusion) as backstops to the aggregate rules (Gate 7 / conventions.md). The cross-
            // aggregate DAG check (N4) needs a graph walk and lives in the application layer.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe_inclusion
                    ADD CONSTRAINT ck_recipe_inclusion_servings CHECK (servings > 0);

                ALTER TABLE recipes.recipe_inclusion
                    ADD CONSTRAINT ck_recipe_inclusion_no_self CHECK (sub_recipe_id <> recipe_id);
            ");

            // Per-household row-level security (ADR-008 / DM-1), consistent with all other recipes tables.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe_inclusion ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.recipe_inclusion FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.recipe_inclusion
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON recipes.recipe_inclusion TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revoke grants and drop the RLS policy first (table must still exist).
            migrationBuilder.Sql(@"
                REVOKE ALL ON recipes.recipe_inclusion FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON recipes.recipe_inclusion;
            ");

            // Dropping the table removes its composite FKs; the referenced uq_recipe_household_recipe
            // anchor belongs to InitialRecipesSchema and is left in place.
            migrationBuilder.DropTable(
                name: "recipe_inclusion",
                schema: "recipes");
        }
    }
}
