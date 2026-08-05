using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Recipes.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubstitution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "substitution",
                schema: "recipes",
                columns: table => new
                {
                    substitution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    target_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    substitute_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    substitute_quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    substitute_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_substitution", x => x.substitution_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_substitution_household_substitute_target",
                schema: "recipes",
                table: "substitution",
                columns: new[] { "household_id", "substitute_product_id", "target_product_id" },
                unique: true);

            // Domain invariants the DB can enforce directly, as a backstop to the aggregate's own
            // Validate check (Gate 7 / conventions.md): no self-substitution, and both quantities
            // strictly positive. No FK to Catalog — target/substitute product/unit ids are soft-refs
            // (DM-3), matching recipe_ingredient's product_id/unit_id.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.substitution
                    ADD CONSTRAINT ck_substitution_no_self CHECK (substitute_product_id != target_product_id);
                ALTER TABLE recipes.substitution
                    ADD CONSTRAINT ck_substitution_target_quantity_positive CHECK (target_quantity > 0);
                ALTER TABLE recipes.substitution
                    ADD CONSTRAINT ck_substitution_substitute_quantity_positive CHECK (substitute_quantity > 0);
                ALTER TABLE recipes.substitution
                    ADD CONSTRAINT ck_substitution_ids_not_empty CHECK (
                        target_product_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
                        target_unit_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
                        substitute_product_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
                        substitute_unit_id <> '00000000-0000-0000-0000-000000000000'::uuid);
            ");

            // Per-household row-level security (ADR-008 / DM-1), consistent with all other recipes tables.
            migrationBuilder.Sql(@"
                ALTER TABLE recipes.substitution ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recipes.substitution FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON recipes.substitution
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON recipes.substitution TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revoke grants and drop the RLS policy first (table must still exist).
            migrationBuilder.Sql(@"
                REVOKE ALL ON recipes.substitution FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON recipes.substitution;
            ");

            migrationBuilder.DropTable(
                name: "substitution",
                schema: "recipes");
        }
    }
}
