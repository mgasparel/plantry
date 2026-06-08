using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    parent_product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_due_days = table.Column<int>(type: "integer", nullable: true),
                    default_due_days_after_opening = table.Column<int>(type: "integer", nullable: true),
                    default_due_days_after_freezing = table.Column<int>(type: "integer", nullable: true),
                    default_due_days_after_thawing = table.Column<int>(type: "integer", nullable: true),
                    has_variants = table.Column<bool>(type: "boolean", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_conversions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    factor = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_conversions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_skus",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    size_quantity = table.Column<decimal>(type: "numeric", nullable: true),
                    size_unit_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_skus", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_conversions_household_id",
                schema: "catalog",
                table: "product_conversions",
                column: "household_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_conversions_product_id",
                schema: "catalog",
                table: "product_conversions",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_skus_household_id",
                schema: "catalog",
                table: "product_skus",
                column: "household_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_skus_product_id",
                schema: "catalog",
                table: "product_skus",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_household_id",
                schema: "catalog",
                table: "products",
                column: "household_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_household_id_name",
                schema: "catalog",
                table: "products",
                columns: new[] { "household_id", "name" },
                unique: true);

            // Composite alternate key so child rows and the self-referencing parent link can be
            // FK-constrained to (household_id, id) — guarantees a product's parent and children
            // always live in the same household, independent of the RLS backstop.
            migrationBuilder.AddUniqueConstraint(
                name: "AK_products_household_id_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "household_id", "id" });

            migrationBuilder.Sql(@"
                ALTER TABLE catalog.products
                    ADD CONSTRAINT ck_products_no_self_parent
                    CHECK (parent_product_id IS NULL OR id <> parent_product_id);
            ");

            migrationBuilder.AddForeignKey(
                name: "FK_products_products_household_id_parent_product_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "household_id", "parent_product_id" },
                principalSchema: "catalog",
                principalTable: "products",
                principalColumns: new[] { "household_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_product_conversions_products_household_id_product_id",
                schema: "catalog",
                table: "product_conversions",
                columns: new[] { "household_id", "product_id" },
                principalSchema: "catalog",
                principalTable: "products",
                principalColumns: new[] { "household_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_product_skus_products_household_id_product_id",
                schema: "catalog",
                table: "product_skus",
                columns: new[] { "household_id", "product_id" },
                principalSchema: "catalog",
                principalTable: "products",
                principalColumns: new[] { "household_id", "id" },
                onDelete: ReferentialAction.Cascade);

            // RLS — same backstop as InitialCatalogSchema: even if the app-layer filter is
            // bypassed, Postgres enforces tenant isolation for every catalog table.
            migrationBuilder.Sql(@"
                ALTER TABLE catalog.products ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.products FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON catalog.products
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE catalog.product_skus ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.product_skus FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON catalog.product_skus
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE catalog.product_conversions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.product_conversions FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON catalog.product_conversions
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON catalog.products, catalog.product_skus, catalog.product_conversions TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON catalog.products, catalog.product_skus, catalog.product_conversions FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON catalog.products;
                DROP POLICY IF EXISTS household_isolation ON catalog.product_skus;
                DROP POLICY IF EXISTS household_isolation ON catalog.product_conversions;
            ");

            migrationBuilder.DropTable(
                name: "product_conversions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_skus",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");
        }
    }
}
