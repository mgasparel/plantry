using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHouseholdExpiryWarningDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expiry_warning_days",
                schema: "identity",
                table: "households");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the column with its original shape (integer NOT NULL DEFAULT 3) — the
            // scaffolded default of 0 would not match what every existing row actually had before
            // the column was dropped, since the CLR property (and its HasDefaultValue(3) mapping)
            // no longer exists in the model to scaffold from.
            migrationBuilder.AddColumn<int>(
                name: "expiry_warning_days",
                schema: "identity",
                table: "households",
                type: "integer",
                nullable: false,
                defaultValue: 3);
        }
    }
}
