using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPricingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pricing");

            migrationBuilder.CreateTable(
                name: "price_observation",
                schema: "pricing",
                columns: table => new
                {
                    observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    merchant_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_observation", x => x.observation_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_price_observation_product",
                schema: "pricing",
                table: "price_observation",
                columns: new[] { "household_id", "product_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_price_observation_sku",
                schema: "pricing",
                table: "price_observation",
                columns: new[] { "household_id", "sku_id", "observed_at" },
                filter: "sku_id IS NOT NULL");

            migrationBuilder.Sql(@"
                ALTER TABLE pricing.price_observation ENABLE ROW LEVEL SECURITY;
                ALTER TABLE pricing.price_observation FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON pricing.price_observation
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA pricing TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA pricing TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA pricing TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA pricing FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA pricing FROM app_user;
                REVOKE USAGE ON SCHEMA pricing FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON pricing.price_observation;
            ");

            migrationBuilder.DropTable(
                name: "price_observation",
                schema: "pricing");
        }
    }
}
