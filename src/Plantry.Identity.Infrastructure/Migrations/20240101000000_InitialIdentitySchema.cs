using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(Plantry.Identity.Infrastructure.PlantryIdentityDbContext))]
    [Migration("20240101000000_InitialIdentitySchema")]
    public partial class InitialIdentitySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "identity");

            migrationBuilder.CreateTable(
                name: "households",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(nullable: false),
                    name = table.Column<string>(maxLength: 200, nullable: false),
                    email_intake_address = table.Column<string>(maxLength: 254, nullable: true),
                    expiry_warning_days = table.Column<int>(nullable: false, defaultValue: 3),
                    theme = table.Column<string>(maxLength: 20, nullable: false, defaultValue: "hearth"),
                    created_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_households", x => x.id));

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    household_id = table.Column<Guid>(nullable: false),
                    display_name = table.Column<string>(maxLength: 100, nullable: false, defaultValue: ""),
                    UserName = table.Column<string>(maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(maxLength: 256, nullable: true),
                    Email = table.Column<string>(maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(nullable: false),
                    PasswordHash = table.Column<string>(nullable: true),
                    SecurityStamp = table.Column<string>(nullable: true),
                    ConcurrencyStamp = table.Column<string>(nullable: true),
                    PhoneNumber = table.Column<string>(nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(nullable: false),
                    TwoFactorEnabled = table.Column<bool>(nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(nullable: true),
                    LockoutEnabled = table.Column<bool>(nullable: false),
                    AccessFailedCount = table.Column<int>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_users", x => x.Id));

            // Identity framework tables (roles, claims, logins, tokens)
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<string>(nullable: false),
                    Name = table.Column<string>(maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_AspNetRoles", x => x.Id));

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(nullable: false),
                    ClaimType = table.Column<string>(nullable: true),
                    ClaimValue = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey("FK_AspNetRoleClaims_AspNetRoles_RoleId", x => x.RoleId, "AspNetRoles", "Id", "identity", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(nullable: false),
                    ClaimType = table.Column<string>(nullable: true),
                    ClaimValue = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey("FK_AspNetUserClaims_users_UserId", x => x.UserId, "users", "Id", "identity", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                schema: "identity",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(nullable: false),
                    ProviderKey = table.Column<string>(nullable: false),
                    ProviderDisplayName = table.Column<string>(nullable: true),
                    UserId = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey("FK_AspNetUserLogins_users_UserId", x => x.UserId, "users", "Id", "identity", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                schema: "identity",
                columns: table => new
                {
                    UserId = table.Column<string>(nullable: false),
                    RoleId = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey("FK_AspNetUserRoles_AspNetRoles_RoleId", x => x.RoleId, "AspNetRoles", "Id", "identity", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey("FK_AspNetUserRoles_users_UserId", x => x.UserId, "users", "Id", "identity", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                schema: "identity",
                columns: table => new
                {
                    UserId = table.Column<string>(nullable: false),
                    LoginProvider = table.Column<string>(nullable: false),
                    Name = table.Column<string>(nullable: false),
                    Value = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey("FK_AspNetUserTokens_users_UserId", x => x.UserId, "users", "Id", "identity", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                schema: "identity",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "identity",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                schema: "identity",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                schema: "identity",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                schema: "identity",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "identity",
                table: "users",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "identity",
                table: "users",
                column: "NormalizedUserName",
                unique: true,
                filter: "\"NormalizedUserName\" IS NOT NULL");

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

            // RLS policies
            migrationBuilder.Sql(@"
                -- Household isolation for users, with a carve-out: when no tenant context is
                -- set (app.household_id unset or empty), all rows are visible. This is required
                -- because ASP.NET Core Identity must look users up *before* a household is known
                -- (login by email, cookie/security-stamp revalidation, and user creation during
                -- registration all run with no household context). Once a household context IS
                -- active, access is restricted to that household — so authenticated, tenant-scoped
                -- queries remain isolated while the framework's pre-auth paths keep working.
                ALTER TABLE identity.users ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.users FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON identity.users
                  USING (
                    NULLIF(current_setting('app.household_id', true), '') IS NULL
                    OR household_id = NULLIF(current_setting('app.household_id', true), '')::uuid
                  );

                -- Isolation for the households table itself (the tenant anchor). It holds
                -- household-scoped sensitive config (name, email_intake_address), so the DB-level
                -- backstop must cover it too. Keyed on the row's own id. Same no-context carve-out
                -- as users: RegisterHouseholdCommand inserts the household row *before* any tenant
                -- context is armed, so that insert must remain visible/writable with no context.
                -- Once a household context is active, only that household's own row is visible.
                ALTER TABLE identity.households ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.households FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON identity.households
                  USING (
                    NULLIF(current_setting('app.household_id', true), '') IS NULL
                    OR id = NULLIF(current_setting('app.household_id', true), '')::uuid
                  );

                GRANT USAGE ON SCHEMA identity TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA identity FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA identity FROM app_user;
                REVOKE USAGE ON SCHEMA identity FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON identity.households;
                DROP POLICY IF EXISTS household_isolation ON identity.users;
            ");
            migrationBuilder.DropTable("AspNetUserTokens", "identity");
            migrationBuilder.DropTable("AspNetUserLogins", "identity");
            migrationBuilder.DropTable("AspNetUserClaims", "identity");
            migrationBuilder.DropTable("AspNetUserRoles", "identity");
            migrationBuilder.DropTable("AspNetRoleClaims", "identity");
            migrationBuilder.DropTable("AspNetRoles", "identity");
            migrationBuilder.DropTable("users", "identity");
            migrationBuilder.DropTable("households", "identity");
        }
    }
}
