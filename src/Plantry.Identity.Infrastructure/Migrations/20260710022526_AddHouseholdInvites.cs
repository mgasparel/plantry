using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "household_invites",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_household_invites", x => x.id);
                    table.CheckConstraint("ck_household_invites_status", "status IN ('pending','accepted','revoked','expired')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_household_invites_household_id",
                schema: "identity",
                table: "household_invites",
                column: "household_id");

            migrationBuilder.CreateIndex(
                name: "ux_household_invites_token",
                schema: "identity",
                table: "household_invites",
                column: "token",
                unique: true);

            // RLS for household_invites, keyed on household_id, with the SAME no-context carve-out as
            // identity.households / identity.users: when app.household_id is unset all rows are visible,
            // otherwise only the active household's invites. The carve-out is load-bearing here — the
            // accept path resolves an invite by token while the invitee is unauthenticated and NO tenant
            // is armed. Once a household context is active (issue/revoke), only its own invites are
            // visible/writable. With USING and no WITH CHECK, Postgres reuses USING for INSERT/UPDATE, so
            // an authenticated member can only insert an invite for their own household.
            //
            // Grants are explicit: the initial-schema GRANT ON ALL TABLES only covered tables that
            // existed then, so this new table needs its own grant to app_user (the runtime role).
            migrationBuilder.Sql(@"
                ALTER TABLE identity.household_invites ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.household_invites FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON identity.household_invites
                  USING (
                    NULLIF(current_setting('app.household_id', true), '') IS NULL
                    OR household_id = NULLIF(current_setting('app.household_id', true), '')::uuid
                  );

                GRANT SELECT, INSERT, UPDATE, DELETE ON identity.household_invites TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON identity.household_invites FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON identity.household_invites;
            ");
            migrationBuilder.DropTable(
                name: "household_invites",
                schema: "identity");
        }
    }
}
