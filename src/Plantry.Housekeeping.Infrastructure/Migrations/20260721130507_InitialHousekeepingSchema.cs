using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Housekeeping.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialHousekeepingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "housekeeping");

            migrationBuilder.CreateTable(
                name: "dismissal",
                schema: "housekeeping",
                columns: table => new
                {
                    dismissal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    detector_id = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facts_fingerprint = table.Column<string>(type: "text", nullable: false),
                    dismissed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dismissal", x => x.dismissal_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_dismissal_household_detector_subject",
                schema: "housekeeping",
                table: "dismissal",
                columns: new[] { "household_id", "detector_id", "subject_id" },
                unique: true);

            // ── Per-household Row Level Security (ADR-008 / T9) ─────────────────────
            migrationBuilder.Sql(@"
                ALTER TABLE housekeeping.dismissal ENABLE ROW LEVEL SECURITY;
                ALTER TABLE housekeeping.dismissal FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON housekeeping.dismissal
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA housekeeping TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA housekeeping TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA housekeeping TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA housekeeping FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA housekeeping FROM app_user;
                REVOKE USAGE ON SCHEMA housekeeping FROM app_user;

                DROP POLICY IF EXISTS household_isolation ON housekeeping.dismissal;
            ");

            migrationBuilder.DropTable(
                name: "dismissal",
                schema: "housekeeping");
        }
    }
}
