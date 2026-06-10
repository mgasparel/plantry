using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialIntakeSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "intake");

            migrationBuilder.CreateTable(
                name: "import_session",
                schema: "intake",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    merchant_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    parse_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    parsed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_session", x => x.session_id);
                });

            migrationBuilder.CreateTable(
                name: "import_line",
                schema: "intake",
                columns: table => new
                {
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    receipt_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    suggested_confidence = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    raw_parse = table.Column<string>(type: "jsonb", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_observation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_product_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_line", x => x.line_id);
                    table.ForeignKey(
                        name: "FK_import_line_import_session_session_id",
                        column: x => x.session_id,
                        principalSchema: "intake",
                        principalTable: "import_session",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_receipt",
                schema: "intake",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raw_text = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_receipt", x => x.session_id);
                    table.ForeignKey(
                        name: "FK_import_receipt_import_session_session_id",
                        column: x => x.session_id,
                        principalSchema: "intake",
                        principalTable: "import_session",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_line_session",
                schema: "intake",
                table: "import_line",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_session_household",
                schema: "intake",
                table: "import_session",
                column: "household_id");

            // Upgrade single-column FKs to composite (household_id, session_id) per G6-2 convention.
            migrationBuilder.Sql(@"
                ALTER TABLE intake.import_session
                    ADD CONSTRAINT uq_import_session_household_session UNIQUE (household_id, session_id);

                ALTER TABLE intake.import_line
                    DROP CONSTRAINT ""FK_import_line_import_session_session_id"";

                ALTER TABLE intake.import_line
                    ADD CONSTRAINT fk_import_line_import_session
                    FOREIGN KEY (household_id, session_id)
                    REFERENCES intake.import_session (household_id, session_id)
                    ON DELETE CASCADE;

                ALTER TABLE intake.import_receipt
                    DROP CONSTRAINT ""FK_import_receipt_import_session_session_id"";

                ALTER TABLE intake.import_receipt
                    ADD CONSTRAINT fk_import_receipt_import_session
                    FOREIGN KEY (household_id, session_id)
                    REFERENCES intake.import_session (household_id, session_id)
                    ON DELETE CASCADE;
            ");

            // source_type is a closed set — enforce it at the DB.
            migrationBuilder.Sql(@"
                ALTER TABLE intake.import_session
                    ADD CONSTRAINT ck_import_session_source_type
                    CHECK (source_type IN ('Receipt'));
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE intake.import_session ENABLE ROW LEVEL SECURITY;
                ALTER TABLE intake.import_session FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON intake.import_session
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE intake.import_line ENABLE ROW LEVEL SECURITY;
                ALTER TABLE intake.import_line FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON intake.import_line
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE intake.import_receipt ENABLE ROW LEVEL SECURITY;
                ALTER TABLE intake.import_receipt FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON intake.import_receipt
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA intake TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA intake TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA intake TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA intake FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA intake FROM app_user;
                REVOKE USAGE ON SCHEMA intake FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON intake.import_session;
                DROP POLICY IF EXISTS household_isolation ON intake.import_line;
                DROP POLICY IF EXISTS household_isolation ON intake.import_receipt;
            ");

            migrationBuilder.DropTable(
                name: "import_line",
                schema: "intake");

            migrationBuilder.DropTable(
                name: "import_receipt",
                schema: "intake");

            migrationBuilder.DropTable(
                name: "import_session",
                schema: "intake");
        }
    }
}
