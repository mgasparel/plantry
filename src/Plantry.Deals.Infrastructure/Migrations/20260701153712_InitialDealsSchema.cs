using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Deals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialDealsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "deals");

            migrationBuilder.CreateTable(
                name: "deal",
                schema: "deals",
                columns: table => new
                {
                    deal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flyer_import_id = table.Column<Guid>(type: "uuid", nullable: true),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    raw_name = table.Column<string>(type: "text", nullable: false),
                    brand = table.Column<string>(type: "text", nullable: true),
                    size = table.Column<string>(type: "text", nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sale_story = table.Column<string>(type: "text", nullable: true),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    suggested_product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match_confidence = table.Column<string>(type: "text", nullable: false),
                    match_reasoning = table.Column<string>(type: "text", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: false),
                    committed_price_observation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    auto_matched = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deal", x => x.deal_id);
                });

            migrationBuilder.CreateTable(
                name: "deal_match_memory",
                schema: "deals",
                columns: table => new
                {
                    deal_match_memory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    raw_name = table.Column<string>(type: "text", nullable: false),
                    normalizer_version = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deal_match_memory", x => x.deal_match_memory_id);
                });

            migrationBuilder.CreateTable(
                name: "flyer_import",
                schema: "deals",
                columns: table => new
                {
                    flyer_import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flyer_external_id = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: false),
                    raw_flyer = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error_detail = table.Column<string>(type: "text", nullable: true),
                    pulled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    parsed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flyer_import", x => x.flyer_import_id);
                });

            migrationBuilder.CreateTable(
                name: "store_subscription",
                schema: "deals",
                columns: table => new
                {
                    store_subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    postal_code = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_pulled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_flyer_external_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_subscription", x => x.store_subscription_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deal_household_flyer_import",
                schema: "deals",
                table: "deal",
                columns: new[] { "household_id", "flyer_import_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deal_household_store_status",
                schema: "deals",
                table: "deal",
                columns: new[] { "household_id", "store_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_deal_match_memory_household_store_name",
                schema: "deals",
                table: "deal_match_memory",
                columns: new[] { "household_id", "store_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_flyer_import_household_id",
                schema: "deals",
                table: "flyer_import",
                columns: new[] { "household_id", "flyer_import_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_flyer_import_household_store_external",
                schema: "deals",
                table: "flyer_import",
                columns: new[] { "household_id", "store_id", "flyer_external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_store_subscription_household_store",
                schema: "deals",
                table: "store_subscription",
                columns: new[] { "household_id", "store_id" },
                unique: true);

            // ── Within-context composite FK: deal → flyer_import (DM-3, DD5) ────────
            // The one enforced cross-aggregate FK in this schema. RESTRICT because flyer_import is
            // retained, never deleted. flyer_import_id is nullable for the deferred manual path (D12);
            // Postgres MATCH SIMPLE (the default) leaves the constraint unenforced when it is null.
            // Created as raw SQL — the four aggregates are flat, so there is no EF navigation.
            migrationBuilder.Sql(@"
                ALTER TABLE deals.deal
                    ADD CONSTRAINT fk_deal_flyer_import_composite
                    FOREIGN KEY (household_id, flyer_import_id)
                    REFERENCES deals.flyer_import (household_id, flyer_import_id)
                    ON DELETE RESTRICT;
            ");

            // ── Domain CHECK constraints (single-row invariants) ───────────────────
            migrationBuilder.Sql(@"
                -- Enum columns match the domain enums, persisted lowercase (data model deals.md).
                ALTER TABLE deals.deal
                    ADD CONSTRAINT ck_deal_source
                    CHECK (source IN ('flyer', 'manual'));

                ALTER TABLE deals.deal
                    ADD CONSTRAINT ck_deal_status
                    CHECK (status IN ('pending', 'confirmed', 'rejected'));

                ALTER TABLE deals.deal
                    ADD CONSTRAINT ck_deal_match_confidence
                    CHECK (match_confidence IN ('high', 'low', 'none'));

                -- DD10: valid_from <= valid_to on the deal window.
                ALTER TABLE deals.deal
                    ADD CONSTRAINT ck_deal_validity_window
                    CHECK (valid_from <= valid_to);

                ALTER TABLE deals.flyer_import
                    ADD CONSTRAINT ck_flyer_import_status
                    CHECK (status IN ('pulling', 'parsed', 'failed'));

                -- DD10: valid_from <= valid_to on the flyer window (copied onto each deal).
                ALTER TABLE deals.flyer_import
                    ADD CONSTRAINT ck_flyer_import_validity_window
                    CHECK (valid_from <= valid_to);
            ");

            // ── Per-household Row Level Security (ADR-008 / DM-1) ───────────────────
            migrationBuilder.Sql(@"
                ALTER TABLE deals.store_subscription ENABLE ROW LEVEL SECURITY;
                ALTER TABLE deals.store_subscription FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON deals.store_subscription
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE deals.flyer_import ENABLE ROW LEVEL SECURITY;
                ALTER TABLE deals.flyer_import FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON deals.flyer_import
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE deals.deal ENABLE ROW LEVEL SECURITY;
                ALTER TABLE deals.deal FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON deals.deal
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE deals.deal_match_memory ENABLE ROW LEVEL SECURITY;
                ALTER TABLE deals.deal_match_memory FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON deals.deal_match_memory
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA deals TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA deals TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA deals TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA deals FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA deals FROM app_user;
                REVOKE USAGE ON SCHEMA deals FROM app_user;

                DROP POLICY IF EXISTS household_isolation ON deals.store_subscription;
                DROP POLICY IF EXISTS household_isolation ON deals.flyer_import;
                DROP POLICY IF EXISTS household_isolation ON deals.deal;
                DROP POLICY IF EXISTS household_isolation ON deals.deal_match_memory;

                ALTER TABLE deals.deal DROP CONSTRAINT IF EXISTS fk_deal_flyer_import_composite;
            ");

            migrationBuilder.DropTable(
                name: "deal",
                schema: "deals");

            migrationBuilder.DropTable(
                name: "deal_match_memory",
                schema: "deals");

            migrationBuilder.DropTable(
                name: "flyer_import",
                schema: "deals");

            migrationBuilder.DropTable(
                name: "store_subscription",
                schema: "deals");
        }
    }
}
