using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialRecipesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "recipes");

            migrationBuilder.CreateTable(
                name: "cook_event",
                schema: "recipes",
                columns: table => new
                {
                    cook_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    servings_cooked = table.Column<int>(type: "integer", nullable: false),
                    cooked_by = table.Column<Guid>(type: "uuid", nullable: false),
                    cooked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cook_event", x => x.cook_event_id);
                });

            migrationBuilder.CreateTable(
                name: "recipe",
                schema: "recipes",
                columns: table => new
                {
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: true),
                    cook_time_minutes = table.Column<int>(type: "integer", nullable: true),
                    default_servings = table.Column<int>(type: "integer", nullable: false),
                    directions = table.Column<string>(type: "text", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe", x => x.recipe_id);
                });

            migrationBuilder.CreateTable(
                name: "recipe_ingredient",
                schema: "recipes",
                columns: table => new
                {
                    ingredient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_heading = table.Column<string>(type: "text", nullable: true),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_ingredient", x => x.ingredient_id);
                });

            migrationBuilder.CreateTable(
                name: "recipe_photo",
                schema: "recipes",
                columns: table => new
                {
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_photo", x => x.recipe_id);
                });

            migrationBuilder.CreateTable(
                name: "recipe_tag",
                schema: "recipes",
                columns: table => new
                {
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_tag", x => new { x.recipe_id, x.tag_id });
                });

            migrationBuilder.CreateTable(
                name: "tag",
                schema: "recipes",
                columns: table => new
                {
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag", x => x.tag_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cook_event_household_recipe_cooked",
                schema: "recipes",
                table: "cook_event",
                columns: new[] { "household_id", "recipe_id", "cooked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_recipe_household_cook_time",
                schema: "recipes",
                table: "recipe",
                columns: new[] { "household_id", "cook_time_minutes" });

            migrationBuilder.CreateIndex(
                name: "ix_recipe_household_created",
                schema: "recipes",
                table: "recipe",
                columns: new[] { "household_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_recipe_household_name",
                schema: "recipes",
                table: "recipe",
                columns: new[] { "household_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_recipe_ingredient_recipe_ordinal",
                schema: "recipes",
                table: "recipe_ingredient",
                columns: new[] { "recipe_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recipe_tag_household_tag",
                schema: "recipes",
                table: "recipe_tag",
                columns: new[] { "household_id", "tag_id" });

            migrationBuilder.CreateIndex(
                name: "ux_tag_household_name",
                schema: "recipes",
                table: "tag",
                columns: new[] { "household_id", "name" },
                unique: true);

            // Tenant-safe child FKs (conventions.md): add the UNIQUE (household_id, id) anchors on the
            // aggregate parents, then point each child at the parent via a composite (household_id, parent_id)
            // FK so a child can never reference another tenant's parent. EF maps no relationships for this
            // schema-only step, so all FKs are created here. Mirrors intake.InitialIntakeSchema.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe
                    ADD CONSTRAINT uq_recipe_household_recipe UNIQUE (household_id, recipe_id);

                ALTER TABLE recipes.tag
                    ADD CONSTRAINT uq_tag_household_tag UNIQUE (household_id, tag_id);

                ALTER TABLE recipes.recipe_ingredient
                    ADD CONSTRAINT fk_recipe_ingredient_recipe
                    FOREIGN KEY (household_id, recipe_id)
                    REFERENCES recipes.recipe (household_id, recipe_id)
                    ON DELETE CASCADE;

                ALTER TABLE recipes.recipe_photo
                    ADD CONSTRAINT fk_recipe_photo_recipe
                    FOREIGN KEY (household_id, recipe_id)
                    REFERENCES recipes.recipe (household_id, recipe_id)
                    ON DELETE CASCADE;

                ALTER TABLE recipes.recipe_tag
                    ADD CONSTRAINT fk_recipe_tag_recipe
                    FOREIGN KEY (household_id, recipe_id)
                    REFERENCES recipes.recipe (household_id, recipe_id)
                    ON DELETE CASCADE;

                -- Tag deletion is not a modelled behaviour; RESTRICT is a conservative backstop.
                ALTER TABLE recipes.recipe_tag
                    ADD CONSTRAINT fk_recipe_tag_tag
                    FOREIGN KEY (household_id, tag_id)
                    REFERENCES recipes.tag (household_id, tag_id)
                    ON DELETE RESTRICT;

                -- Recipe is soft-deleted (archived_at), never physically removed, so RESTRICT never fires
                -- yet keeps every append-only cook_event FK valid (Resolved call 1 / R8).
                ALTER TABLE recipes.cook_event
                    ADD CONSTRAINT fk_cook_event_recipe
                    FOREIGN KEY (household_id, recipe_id)
                    REFERENCES recipes.recipe (household_id, recipe_id)
                    ON DELETE RESTRICT;
            ");

            // Domain CHECK constraints that the DB can enforce directly (R2 / R5 / C2). The narrower
            // app-layer rules (untracked-staple null, unit-conversion path, ordinal contiguity, ≥1 ingredient)
            // need cross-context reads and live in the P2-1 authoring service, not here.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe
                    ADD CONSTRAINT ck_recipe_default_servings CHECK (default_servings >= 1);

                ALTER TABLE recipes.recipe_ingredient
                    ADD CONSTRAINT ck_recipe_ingredient_quantity_unit
                    CHECK ((quantity IS NULL) = (unit_id IS NULL));

                ALTER TABLE recipes.cook_event
                    ADD CONSTRAINT ck_cook_event_servings_cooked CHECK (servings_cooked >= 1);

                ALTER TABLE recipes.tag
                    ADD CONSTRAINT ck_tag_category
                    CHECK (category IN ('Diet', 'Protein', 'Flavor', 'Cuisine'));
            ");

            // Per-household row-level security on every table (ADR-008 / DM-1), including the photo and
            // join tables. FORCE so the policy applies even to the table owner; the app connects as the
            // non-owner app_user role at runtime so the USING clause actually gates rows.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.recipe ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.recipe FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.recipe
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE recipes.recipe_ingredient ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.recipe_ingredient FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.recipe_ingredient
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE recipes.recipe_photo ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.recipe_photo FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.recipe_photo
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE recipes.cook_event ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.cook_event FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.cook_event
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE recipes.tag ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.tag FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.tag
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE recipes.recipe_tag ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.recipe_tag FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.recipe_tag
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA recipes TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA recipes TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA recipes TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA recipes FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA recipes FROM app_user;
                REVOKE USAGE ON SCHEMA recipes FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON recipes.recipe;
                DROP POLICY IF EXISTS household_isolation ON recipes.recipe_ingredient;
                DROP POLICY IF EXISTS household_isolation ON recipes.recipe_photo;
                DROP POLICY IF EXISTS household_isolation ON recipes.cook_event;
                DROP POLICY IF EXISTS household_isolation ON recipes.tag;
                DROP POLICY IF EXISTS household_isolation ON recipes.recipe_tag;
            ");

            // Children (composite-FK holders) before parents — the FKs are raw-SQL so EF can't order this.
            migrationBuilder.DropTable(
                name: "cook_event",
                schema: "recipes");

            migrationBuilder.DropTable(
                name: "recipe_ingredient",
                schema: "recipes");

            migrationBuilder.DropTable(
                name: "recipe_photo",
                schema: "recipes");

            migrationBuilder.DropTable(
                name: "recipe_tag",
                schema: "recipes");

            migrationBuilder.DropTable(
                name: "recipe",
                schema: "recipes");

            migrationBuilder.DropTable(
                name: "tag",
                schema: "recipes");
        }
    }
}
