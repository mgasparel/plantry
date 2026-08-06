using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Composition.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillYieldProductsIsProduced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time backfill (plantry-sn6v): households that cooked recipes with a yield before
            // catalog.products.is_produced existed have yield/leftover products still flagged as
            // ordinary purchases, so they read as restock candidates once eaten down to zero.
            //
            // Placed in Housekeeping (like 20260727062625_DeletePackAndDozenUnits) because it is the
            // LAST entry in Plantry.Migrator/MigrationTargets.All ("ORDER IS LOAD-BEARING") — the only
            // migration guaranteed to run after Catalog's is_produced column AND Recipes'
            // yield_product_id data both already exist. A Catalog-schema migration (second in the
            // registry, right after Identity) cannot safely reference recipes.recipes: on a fresh
            // database the recipes schema does not exist yet at that point in the run.
            //
            // Known (small) risk, called out per design review: this cannot distinguish "auto-created
            // by AuthorRecipe/CookRecipe" (should be flagged) from "author explicitly chose an existing
            // product as the yield target" (should NOT be flagged, since that product may well be
            // bought too) — the distinguishing fact was never persisted before this ticket. Every
            // current recipes.recipes.yield_product_id is flagged; a household in the latter case sees
            // that product drop out of restock suggestions until they clear the flag from the product
            // editor (Catalog/Products/Detail — plantry-sn6v's "Homemade" toggle).
            migrationBuilder.Sql(
                """
                UPDATE catalog.products
                SET is_produced = true
                WHERE id IN (
                    SELECT yield_product_id FROM recipes.recipe WHERE yield_product_id IS NOT NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only migration, not reversed — see DeletePackAndDozenUnits.Down for rationale
            // (also: reversing would incorrectly clear IsProduced on any product a user has since
            // flagged manually from the editor, which this migration cannot distinguish from its own
            // backfill).
        }
    }
}
