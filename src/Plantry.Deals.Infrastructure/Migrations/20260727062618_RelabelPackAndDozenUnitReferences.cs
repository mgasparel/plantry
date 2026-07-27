using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Deals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RelabelPackAndDozenUnitReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time data migration (plantry-qszb): Catalog no longer seeds 'pk'/'doz' — see
            // Plantry.Catalog.Infrastructure/Migrations/20260727061526_RemovePackAndDozenUnits.cs for
            // the full rationale. This migration relabels Deals' own soft unit_id reference (DM-3: no
            // enforced cross-context FK to catalog.units) from pk/doz to that household's 'ea' unit,
            // so nothing here dangles once Housekeeping's DeletePackAndDozenUnits migration (the last
            // entry in MigrationTargets.All) removes the pk/doz rows. Runs after Catalog in
            // MigrationTargets order, so catalog.units — and the still-present pk/doz rows — already
            // exist when this runs.
            const string dozPkCte =
                "WITH doz_pk AS ( " +
                "    SELECT id, household_id FROM catalog.units WHERE lower(symbol) IN ('pk','doz') " +
                "), ea_units AS ( " +
                "    SELECT household_id, id AS ea_id FROM catalog.units WHERE lower(symbol) = 'ea' " +
                ") ";

            migrationBuilder.Sql(dozPkCte +
                "UPDATE deals.deal dl SET unit_id = e.ea_id " +
                "FROM doz_pk d JOIN ea_units e ON e.household_id = d.household_id " +
                "WHERE dl.unit_id = d.id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only migration, not reversed — see RemovePackAndDozenUnits.Down for rationale.
        }
    }
}
