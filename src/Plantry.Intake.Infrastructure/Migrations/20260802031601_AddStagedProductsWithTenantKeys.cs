using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStagedProductsWithTenantKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "staged_product_id",
                schema: "intake",
                table: "import_line",
                type: "uuid",
                nullable: true);

            // The initial Intake migration installs this alternate key while upgrading the original
            // single-column child FKs. Keep the migration safe for databases that already have it and
            // repair older development databases that were created without the upgrade SQL.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'uq_import_session_household_session'
                          AND conrelid = 'intake.import_session'::regclass
                    ) THEN
                        ALTER TABLE intake.import_session
                            ADD CONSTRAINT uq_import_session_household_session
                            UNIQUE (household_id, session_id);
                    END IF;
                END $$;
            ");

            migrationBuilder.CreateTable(
                name: "staged_product",
                schema: "intake",
                columns: table => new
                {
                    staged_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_product_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staged_product", x => x.staged_product_id);
                    table.UniqueConstraint("uq_staged_product_household_session_id", x => new { x.household_id, x.session_id, x.staged_product_id });
                    table.ForeignKey(
                        name: "FK_staged_product_import_session_household_id_session_id",
                        columns: x => new { x.household_id, x.session_id },
                        principalSchema: "intake",
                        principalTable: "import_session",
                        principalColumns: new[] { "household_id", "session_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_import_receipt_household_id_session_id",
                schema: "intake",
                table: "import_receipt",
                columns: new[] { "household_id", "session_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_line_household_id_session_id_staged_product_id",
                schema: "intake",
                table: "import_line",
                columns: new[] { "household_id", "session_id", "staged_product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_line_staged_product",
                schema: "intake",
                table: "import_line",
                column: "staged_product_id");

            migrationBuilder.CreateIndex(
                name: "ix_staged_product_session",
                schema: "intake",
                table: "staged_product",
                columns: new[] { "household_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "uq_staged_product_household_id",
                schema: "intake",
                table: "staged_product",
                columns: new[] { "household_id", "staged_product_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_import_line_staged_product_household_id_session_id_staged_p~",
                schema: "intake",
                table: "import_line",
                columns: new[] { "household_id", "session_id", "staged_product_id" },
                principalSchema: "intake",
                principalTable: "staged_product",
                principalColumns: new[] { "household_id", "session_id", "staged_product_id" },
                onDelete: ReferentialAction.Restrict);

            // import_line and import_receipt already carry composite household/session FKs from the
            // initial schema. They intentionally have no EF operations here: dropping/re-adding them
            // would make this migration depend on provider-generated constraint names.

            migrationBuilder.Sql(@"
                ALTER TABLE intake.staged_product ENABLE ROW LEVEL SECURITY;
                ALTER TABLE intake.staged_product FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON intake.staged_product
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);
                GRANT SELECT, INSERT, UPDATE, DELETE ON intake.staged_product TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_import_line_staged_product_household_id_session_id_staged_p~",
                schema: "intake",
                table: "import_line");

            migrationBuilder.Sql(@"
                REVOKE ALL ON intake.staged_product FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON intake.staged_product;
            ");

            migrationBuilder.DropTable(
                name: "staged_product",
                schema: "intake");

            migrationBuilder.DropIndex(
                name: "IX_import_receipt_household_id_session_id",
                schema: "intake",
                table: "import_receipt");

            migrationBuilder.DropIndex(
                name: "IX_import_line_household_id_session_id_staged_product_id",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropIndex(
                name: "ix_import_line_staged_product",
                schema: "intake",
                table: "import_line");

            migrationBuilder.DropColumn(
                name: "staged_product_id",
                schema: "intake",
                table: "import_line");

            // The initial schema owns the composite parent FKs and alternate key; leave them intact
            // when this feature migration is rolled back.
        }
    }
}
