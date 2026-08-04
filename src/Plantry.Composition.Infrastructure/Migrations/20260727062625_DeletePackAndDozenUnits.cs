using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Composition.Infrastructure.Migrations
{
    /// <inheritdoc />
    // plantry-g3da.2 (ADR-024 Phase A): fully-qualified — see InitialHousekeepingSchema.cs's comment.
    public partial class DeletePackAndDozenUnits : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time data migration (plantry-qszb): final step of the pk/doz retirement started in
            // Plantry.Pantry.Infrastructure/Migrations/20260727061526_RemovePackAndDozenUnits.cs — see
            // that file for the full rationale. By the time this migration runs, every bounded context
            // holding a soft unit_id reference to catalog.units (Catalog itself, Inventory, Pricing,
            // Intake, Recipes, Shopping, Deals) has already relabeled its own pk/doz references to 'ea'
            // in its own migration — this one is deliberately placed in Housekeeping because it is the
            // LAST entry in Plantry.Migrator/MigrationTargets.All ("ORDER IS LOAD-BEARING"), so it is
            // the only migration guaranteed to run after every other context has finished relabeling.
            // Deleting catalog.units rows here (rather than in Catalog's own migration, which runs
            // second — right after Identity, before any downstream context's relabel has happened)
            // avoids leaving any consumer pointing at a unit id that no longer exists.
            migrationBuilder.Sql(
                "DELETE FROM catalog.units WHERE lower(symbol) IN ('pk','doz');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only migration, not reversed — see RemovePackAndDozenUnits.Down for rationale.
        }
    }
}
