using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemovePackAndDozenUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time data migration (plantry-qszb): CatalogReferenceDataSeeder no longer seeds 'pk'
            // (pack) / 'doz' (dozen) — UnitConverter.BuildConversionGraph never gave Count-dimension
            // units a free same-dimension ratio (plantry-xddq), so doz's seeded FactorToBase=12 was
            // always inert/misleading: ea<->doz/pk always required an explicit ProductConversion, same
            // as any user-created count unit. Rather than special-case doz with a "universal ratio",
            // every existing household's 'pk'/'doz' units — and every reference to them — are relabeled
            // to that household's 'ea' unit. This is a straight relabel, NOT a x12/scaled conversion:
            // Inventory/UnitConverter never actually honored doz's factor, so there is no "real"
            // quantity to preserve by scaling.
            //
            // This migration relabels ONLY Catalog's own references (products, product_skus,
            // product_conversions). Every other bounded context that holds a soft unit_id reference
            // (DM-3: no enforced cross-context FKs) relabels its own tables in its own migration —
            // see Inventory/Pricing/Intake/Recipes/Shopping/Deals' "RelabelPackAndDozenUnitReferences"
            // migrations. This split is required by MigrationTargets' load-bearing order
            // (Plantry.Migrator/MigrationTargets.cs): Catalog's migrations run second, right after
            // Identity, so no other context's schema exists yet when THIS migration runs — a single
            // migration here referencing e.g. inventory.stock_entry would 42P01 on a fresh install.
            // The actual catalog.units pk/doz rows are deleted last, by Housekeeping's migration (the
            // final entry in MigrationTargets.All), once every consuming context has relabeled away
            // from them.
            //
            // Matched case-insensitively by unit code (stored in the `symbol` column), mirroring
            // 20260711152653_AddUnitSystem.cs / 20260724132700_AddServingUnit.cs.
            const string dozPkCte =
                "WITH doz_pk AS ( " +
                "    SELECT id, household_id FROM catalog.units WHERE lower(symbol) IN ('pk','doz') " +
                "), ea_units AS ( " +
                "    SELECT household_id, id AS ea_id FROM catalog.units WHERE lower(symbol) = 'ea' " +
                ") ";

            migrationBuilder.Sql(dozPkCte +
                "UPDATE catalog.products p SET default_unit_id = e.ea_id " +
                "FROM doz_pk d JOIN ea_units e ON e.household_id = d.household_id " +
                "WHERE p.default_unit_id = d.id;");

            migrationBuilder.Sql(dozPkCte +
                "UPDATE catalog.product_skus s SET size_unit_id = e.ea_id " +
                "FROM doz_pk d JOIN ea_units e ON e.household_id = d.household_id " +
                "WHERE s.size_unit_id = d.id;");

            // Relabeling a product_conversions row's pk/doz side onto 'ea' can collide with a row that
            // already targets the same (product_id, from_unit_id, to_unit_id) pair post-relabel — e.g.
            // a household with both `doz->g = 600` and `ea->g = 50` on the same product ends up with
            // two `ea->g` rows once `doz` relabels, and UnitConverter.Convert (BFS, first-matching-edge)
            // silently picks whichever EF materializes first. Drop the pk/doz-sourced row in that case,
            // keeping the pre-existing (already-'ea') row, BEFORE the relabel UPDATEs below run.
            migrationBuilder.Sql(dozPkCte +
                "DELETE FROM catalog.product_conversions c " +
                "USING doz_pk d JOIN ea_units e ON e.household_id = d.household_id " +
                "WHERE (c.from_unit_id = d.id OR c.to_unit_id = d.id) " +
                "  AND EXISTS ( " +
                "    SELECT 1 FROM catalog.product_conversions o " +
                "    WHERE o.product_id = c.product_id AND o.id <> c.id " +
                "      AND o.from_unit_id <> d.id AND o.to_unit_id <> d.id " +
                "      AND o.from_unit_id = (CASE WHEN c.from_unit_id = d.id THEN e.ea_id ELSE c.from_unit_id END) " +
                "      AND o.to_unit_id   = (CASE WHEN c.to_unit_id   = d.id THEN e.ea_id ELSE c.to_unit_id   END));");

            migrationBuilder.Sql(dozPkCte +
                "UPDATE catalog.product_conversions c SET from_unit_id = e.ea_id " +
                "FROM doz_pk d JOIN ea_units e ON e.household_id = d.household_id " +
                "WHERE c.from_unit_id = d.id;");

            migrationBuilder.Sql(dozPkCte +
                "UPDATE catalog.product_conversions c SET to_unit_id = e.ea_id " +
                "FROM doz_pk d JOIN ea_units e ON e.household_id = d.household_id " +
                "WHERE c.to_unit_id = d.id;");

            // Relabeling both sides of a product_conversions row to 'ea' can leave a self-conversion
            // (from_unit_id = to_unit_id) — e.g. a household with an explicit doz->ea conversion that
            // becomes ea->ea once doz is relabeled. Product.AddConversion's Create() forbids this
            // invariant going forward (ProductConversion.Create throws on fromUnitId == toUnitId); a
            // self-conversion left behind by a raw-SQL relabel is meaningless, so it is dropped. This is
            // distinct from the duplicate-pair collision handled above: this catches from==to on a
            // SINGLE row (both sides were pk/doz); the DELETE above catches two DIFFERENT rows landing
            // on the same pair after only one side relabels.
            migrationBuilder.Sql(
                "DELETE FROM catalog.product_conversions WHERE from_unit_id = to_unit_id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only migration, not reversed (mirrors AddUnitSystem/AddServingUnit's Down convention).
            // Reversing every relabeled reference back to its original pk/doz unit is a manual data
            // operation if ever needed — the relabel is intentionally lossy (the "real" pk/doz identity
            // of a row is not recoverable once collapsed onto 'ea').
        }
    }
}
