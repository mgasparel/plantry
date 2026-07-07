using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Deals.Infrastructure.Migrations
{
    /// <summary>
    /// Intentionally empty (no DDL). Remaps <c>FlyerImport.ValidityWindow</c> and <c>Deal.ValidityWindow</c>
    /// from an EF owned entity to a complex type (plantry-cegw). Both mappings produce the identical inline
    /// <c>valid_from</c>/<c>valid_to date NOT NULL</c> columns, so nothing changes in the database — this
    /// migration exists only to advance the model snapshot so future <c>migrations add</c> diffs stay clean.
    /// </summary>
    public partial class MapValidityWindowAsComplexType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op: owned entity → complex type is a model-only change; the columns are unchanged.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: reverting to the owned-entity mapping likewise touches no columns.
        }
    }
}
