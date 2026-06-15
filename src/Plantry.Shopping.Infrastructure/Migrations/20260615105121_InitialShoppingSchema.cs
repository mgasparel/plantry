using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Shopping.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialShoppingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shopping");

            migrationBuilder.CreateTable(
                name: "shopping_list",
                schema: "shopping",
                columns: table => new
                {
                    shopping_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shopping_list", x => x.shopping_list_id);
                });

            migrationBuilder.CreateTable(
                name: "shopping_list_item",
                schema: "shopping",
                columns: table => new
                {
                    shopping_list_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shopping_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    free_text = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shopping_list_item", x => x.shopping_list_item_id);
                    table.ForeignKey(
                        name: "FK_shopping_list_item_shopping_list_shopping_list_id",
                        column: x => x.shopping_list_id,
                        principalSchema: "shopping",
                        principalTable: "shopping_list",
                        principalColumn: "shopping_list_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_shopping_list_household_list",
                schema: "shopping",
                table: "shopping_list",
                columns: new[] { "household_id", "shopping_list_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shopping_list_item_household_list",
                schema: "shopping",
                table: "shopping_list_item",
                columns: new[] { "household_id", "shopping_list_id" });

            migrationBuilder.CreateIndex(
                name: "IX_shopping_list_item_shopping_list_id",
                schema: "shopping",
                table: "shopping_list_item",
                column: "shopping_list_id");

            // Upgrade the EF-created single-column FK to a composite (household_id, shopping_list_id) FK
            // so children carry the tenant anchor per G6-2 convention (mirroring IntakeDbContext pattern).
            migrationBuilder.Sql(@"
                ALTER TABLE shopping.shopping_list_item
                    DROP CONSTRAINT ""FK_shopping_list_item_shopping_list_shopping_list_id"";

                ALTER TABLE shopping.shopping_list_item
                    ADD CONSTRAINT fk_shopping_list_item_shopping_list
                    FOREIGN KEY (household_id, shopping_list_id)
                    REFERENCES shopping.shopping_list (household_id, shopping_list_id)
                    ON DELETE CASCADE;
            ");

            // Item shape constraint: exactly one of product_id / free_text must be non-null (shopping.md, resolved call 3).
            migrationBuilder.Sql(@"
                ALTER TABLE shopping.shopping_list_item
                    ADD CONSTRAINT ck_shopping_list_item_product_or_free_text
                    CHECK (num_nonnulls(product_id, free_text) = 1);
            ");

            // Source provenance: closed set CHECK + default 'manual' (shopping.md §source column).
            migrationBuilder.Sql(@"
                ALTER TABLE shopping.shopping_list_item
                    ADD CONSTRAINT ck_shopping_list_item_source
                    CHECK (source IN ('manual', 'recipe', 'meal_plan', 'deal'));

                ALTER TABLE shopping.shopping_list_item
                    ALTER COLUMN source SET DEFAULT 'manual';
            ");

            // Row-level security — mirrors InitialIntakeSchema pattern.
            migrationBuilder.Sql(@"
                ALTER TABLE shopping.shopping_list ENABLE ROW LEVEL SECURITY;
                ALTER TABLE shopping.shopping_list FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON shopping.shopping_list
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE shopping.shopping_list_item ENABLE ROW LEVEL SECURITY;
                ALTER TABLE shopping.shopping_list_item FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON shopping.shopping_list_item
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA shopping TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA shopping TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA shopping TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA shopping FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA shopping FROM app_user;
                REVOKE USAGE ON SCHEMA shopping FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON shopping.shopping_list;
                DROP POLICY IF EXISTS household_isolation ON shopping.shopping_list_item;
            ");

            migrationBuilder.DropTable(
                name: "shopping_list_item",
                schema: "shopping");

            migrationBuilder.DropTable(
                name: "shopping_list",
                schema: "shopping");
        }
    }
}
