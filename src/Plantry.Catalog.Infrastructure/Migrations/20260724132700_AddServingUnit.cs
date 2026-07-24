using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServingUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time data migration (plantry-n1za, recipe-composition.md §9): backfill the seeded
            // 'srv'/serving count unit onto every household created before this feature, following the
            // precedent of 20260711152653_AddUnitSystem.cs (which classified seeded units for
            // pre-existing households). No schema change — CatalogReferenceDataSeeder already adds this
            // unit for households created from here on.
            //
            // Idempotency guard is on code 'srv' only (matched case-insensitively via the `symbol`
            // column) — re-running this migration inserts nothing new. No name-based dedup against a
            // user-authored "portion"/"serving" unit (decided 2026-07-23: single-user install today;
            // reconciled manually if needed).
            migrationBuilder.Sql(
                "INSERT INTO catalog.units " +
                "(id, household_id, symbol, name, dimension, factor_to_base, is_base, display_style, unit_system) " +
                "SELECT gen_random_uuid(), h.household_id, 'srv', 'serving', 'count', 1, false, 'decimal', 'unspecified' " +
                "FROM (SELECT DISTINCT household_id FROM catalog.units) h " +
                "WHERE NOT EXISTS ( " +
                "    SELECT 1 FROM catalog.units u " +
                "    WHERE u.household_id = h.household_id AND lower(u.symbol) = 'srv' " +
                ");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only migration, not reversed — mirrors AddUnitSystem's Down (schema-only rollback
            // convention). Removing the seeded unit is a manual data operation if ever needed.
        }
    }
}
