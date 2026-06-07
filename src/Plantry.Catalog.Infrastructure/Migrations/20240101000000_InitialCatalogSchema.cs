using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(Plantry.Catalog.Infrastructure.CatalogDbContext))]
    [Migration("20240101000000_InitialCatalogSchema")]
    public partial class InitialCatalogSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "catalog");

            migrationBuilder.CreateTable(
                name: "units",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(nullable: false),
                    household_id = table.Column<Guid>(nullable: false),
                    name = table.Column<string>(maxLength: 100, nullable: false),
                    symbol = table.Column<string>(maxLength: 20, nullable: false),
                    dimension = table.Column<string>(maxLength: 30, nullable: false),
                    factor_to_base = table.Column<decimal>(nullable: false),
                    is_base = table.Column<bool>(nullable: false, defaultValue: false)
                },
                constraints: table => table.PrimaryKey("PK_units", x => x.id));

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(nullable: false),
                    household_id = table.Column<Guid>(nullable: false),
                    name = table.Column<string>(maxLength: 100, nullable: false),
                    default_due_days = table.Column<int>(nullable: true),
                    sort_order = table.Column<int>(nullable: false, defaultValue: 0)
                },
                constraints: table => table.PrimaryKey("PK_categories", x => x.id));

            migrationBuilder.CreateTable(
                name: "locations",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(nullable: false),
                    household_id = table.Column<Guid>(nullable: false),
                    name = table.Column<string>(maxLength: 100, nullable: false),
                    location_type = table.Column<string>(maxLength: 20, nullable: false, defaultValue: "ambient")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.id);
                    table.CheckConstraint("CK_locations_type", "location_type IN ('ambient','frozen')");
                });

            migrationBuilder.CreateIndex("IX_units_household", "units", "household_id", schema: "catalog");
            migrationBuilder.CreateIndex("IX_categories_household", "categories", "household_id", schema: "catalog");
            migrationBuilder.CreateIndex("IX_locations_household", "locations", "household_id", schema: "catalog");

            // Non-superuser application role: RLS never applies to superusers (FORCE included),
            // so the app must connect as a regular role for the RLS backstop to mean anything.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'app_user') THEN
                        CREATE ROLE app_user LOGIN PASSWORD 'app_user_password' NOSUPERUSER NOBYPASSRLS;
                    END IF;
                END
                $$;
            ");

            // RLS policies — backstop: even if app-layer filter is bypassed, Postgres enforces tenant isolation
            migrationBuilder.Sql(@"
                -- Strict per-household isolation. NULLIF(...,'') treats an unset OR empty
                -- app.household_id as 'no tenant', which yields NULL (no rows visible) rather
                -- than an invalid-uuid cast error. Catalog is pure tenant data, so a connection
                -- with no household context can see nothing.
                ALTER TABLE catalog.units ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.units FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON catalog.units
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE catalog.categories ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.categories FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON catalog.categories
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE catalog.locations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.locations FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON catalog.locations
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA catalog TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA catalog TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA catalog TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA catalog FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA catalog FROM app_user;
                REVOKE USAGE ON SCHEMA catalog FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON catalog.units;
                DROP POLICY IF EXISTS household_isolation ON catalog.categories;
                DROP POLICY IF EXISTS household_isolation ON catalog.locations;
            ");
            migrationBuilder.DropTable("units", "catalog");
            migrationBuilder.DropTable("categories", "catalog");
            migrationBuilder.DropTable("locations", "catalog");
        }
    }
}
