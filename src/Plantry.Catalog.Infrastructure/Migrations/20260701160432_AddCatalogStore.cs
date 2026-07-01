using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stores",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stores", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stores_household_id_external_ref",
                schema: "catalog",
                table: "stores",
                columns: new[] { "household_id", "external_ref" },
                unique: true,
                filter: "external_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_stores_household_id_name",
                schema: "catalog",
                table: "stores",
                columns: new[] { "household_id", "name" },
                unique: true);

            // RLS — same backstop as the other catalog tables: even if the app-layer filter is
            // bypassed, Postgres enforces per-household isolation for catalog.stores.
            migrationBuilder.Sql(@"
                ALTER TABLE catalog.stores ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.stores FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON catalog.stores
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON catalog.stores TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON catalog.stores FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON catalog.stores;
            ");

            migrationBuilder.DropTable(
                name: "stores",
                schema: "catalog");
        }
    }
}
