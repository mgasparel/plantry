using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pantry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPantrySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    default_due_days = table.Column<int>(type: "integer", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    hue = table.Column<int>(type: "integer", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "household_inventory_settings",
                schema: "inventory",
                columns: table => new
                {
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiring_soon_days = table.Column<int>(type: "integer", nullable: false),
                    default_location_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_household_inventory_settings", x => x.household_id);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    location_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_stock",
                schema: "inventory",
                columns: table => new
                {
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    low_stock_threshold = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_stock", x => new { x.household_id, x.product_id });
                });

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
                    track_stock = table.Column<bool>(type: "boolean", nullable: false),
                    is_produced = table.Column<bool>(type: "boolean", nullable: false),
                    default_due_days = table.Column<int>(type: "integer", nullable: true),
                    default_due_days_after_opening = table.Column<int>(type: "integer", nullable: true),
                    default_due_days_after_freezing = table.Column<int>(type: "integer", nullable: true),
                    default_due_days_after_thawing = table.Column<int>(type: "integer", nullable: true),
                    never_expires_after_freezing = table.Column<bool>(type: "boolean", nullable: true),
                    never_expires_after_thawing = table.Column<bool>(type: "boolean", nullable: true),
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

            migrationBuilder.CreateTable(
                name: "units",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    dimension = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    factor_to_base = table.Column<decimal>(type: "numeric", nullable: false),
                    is_base = table.Column<bool>(type: "boolean", nullable: false),
                    // Column-level DEFAULT carried over from AddUnitDisplayStyle / AddUnitSystem — a
                    // legacy row that predates the column always defaulted here; EF inserts always send
                    // an explicit value, so this only backstops raw-SQL writers (test harnesses, ad hoc
                    // ops scripts) that omit the column.
                    display_style = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "decimal"),
                    unit_system = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "unspecified")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_units", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_entry",
                schema: "inventory",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    frozen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    thawed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purchased_at = table.Column<DateOnly>(type: "date", nullable: true),
                    depleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_entry", x => x.entry_id);
                    table.ForeignKey(
                        name: "FK_stock_entry_product_stock_household_id_product_id",
                        columns: x => new { x.household_id, x.product_id },
                        principalSchema: "inventory",
                        principalTable: "product_stock",
                        principalColumns: new[] { "household_id", "product_id" },
                        onDelete: ReferentialAction.Cascade);
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
                    factor = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    // Column-level DEFAULT carried over from AddProductConversionSource, same rationale
                    // as units.display_style/unit_system above.
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "user_confirmed")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_conversions", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_conversions_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
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
                    size_quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    size_unit_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_skus", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_skus_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_journal_entry",
                schema: "inventory",
                columns: table => new
                {
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    household_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delta = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    source_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    source_line_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_journal_entry", x => x.journal_id);
                    table.ForeignKey(
                        name: "FK_stock_journal_entry_product_stock_household_id_product_id",
                        columns: x => new { x.household_id, x.product_id },
                        principalSchema: "inventory",
                        principalTable: "product_stock",
                        principalColumns: new[] { "household_id", "product_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stock_journal_entry_stock_entry_entry_id",
                        column: x => x.entry_id,
                        principalSchema: "inventory",
                        principalTable: "stock_entry",
                        principalColumn: "entry_id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_categories_household_id_name",
                schema: "catalog",
                table: "categories",
                columns: new[] { "household_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_locations_household_id_name",
                schema: "catalog",
                table: "locations",
                columns: new[] { "household_id", "name" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_stock_entry_by_location",
                schema: "inventory",
                table: "stock_entry",
                columns: new[] { "household_id", "location_id", "product_id" },
                filter: "depleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_entry_fefo",
                schema: "inventory",
                table: "stock_entry",
                columns: new[] { "household_id", "product_id", "expiry_date", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_journal_entry_entry_id",
                schema: "inventory",
                table: "stock_journal_entry",
                column: "entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_journal_entry_household_id",
                schema: "inventory",
                table: "stock_journal_entry",
                column: "household_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_journal_entry_household_id_product_id",
                schema: "inventory",
                table: "stock_journal_entry",
                columns: new[] { "household_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_journal_idempotency",
                schema: "inventory",
                table: "stock_journal_entry",
                columns: new[] { "household_id", "source_ref", "source_line_ref" });

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

            migrationBuilder.CreateIndex(
                name: "IX_units_household_id_symbol",
                schema: "catalog",
                table: "units",
                columns: new[] { "household_id", "symbol" },
                unique: true);

            // ── Composite tenancy constraints (raw SQL, not part of the EF fluent model). These are
            // re-homed UNCHANGED from AddProductTables (products self-parent + the two Product
            // children's household-scoped FKs) and AddCompositeStockEntryFk (the journal→stock_entry
            // FK) — every one was originally added outside the fluent model (CatalogDbContextModelSnapshot
            // / InventoryDbContextModelSnapshot only ever recorded the plain single-column FKs EF itself
            // scaffolds), so PantryDbContext's OnModelCreating and PantryDbContextModelSnapshot correctly
            // do NOT represent them — same rationale as the CHECK constraints and the unordered-pair
            // index below. Mirrors InitialMarketSchema's re-homing of fk_deal_flyer_import_composite.
            migrationBuilder.Sql(@"
                ALTER TABLE catalog.products
                    ADD CONSTRAINT ""AK_products_household_id_id"" UNIQUE (household_id, id);

                ALTER TABLE catalog.products
                    ADD CONSTRAINT ""FK_products_products_household_id_parent_product_id""
                    FOREIGN KEY (household_id, parent_product_id)
                    REFERENCES catalog.products (household_id, id);

                ALTER TABLE catalog.product_conversions
                    DROP CONSTRAINT ""FK_product_conversions_products_product_id"";
                ALTER TABLE catalog.product_conversions
                    ADD CONSTRAINT ""FK_product_conversions_products_household_id_product_id""
                    FOREIGN KEY (household_id, product_id)
                    REFERENCES catalog.products (household_id, id)
                    ON DELETE CASCADE;

                ALTER TABLE catalog.product_skus
                    DROP CONSTRAINT ""FK_product_skus_products_product_id"";
                ALTER TABLE catalog.product_skus
                    ADD CONSTRAINT ""FK_product_skus_products_household_id_product_id""
                    FOREIGN KEY (household_id, product_id)
                    REFERENCES catalog.products (household_id, id)
                    ON DELETE CASCADE;

                ALTER TABLE inventory.stock_entry
                    ADD CONSTRAINT uq_stock_entry_household_entry UNIQUE (household_id, entry_id);

                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT ""FK_stock_journal_entry_stock_entry_entry_id"";
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT fk_stock_journal_entry_stock_entry
                    FOREIGN KEY (household_id, entry_id)
                    REFERENCES inventory.stock_entry (household_id, entry_id);
            ");

            // ── CHECK constraints (raw SQL, not part of the EF fluent model — see PantryDbContext's
            // OnModelCreating remarks on the product_conversions unordered-pair index below for why).
            // Every value here is the FINAL allow-list carried over unchanged from the incremental
            // migrations this baseline replaces: CatalogDbContext's AddProductTables /
            // AddProductConversionSource / AddUnitDisplayStyle / AddUnitSystem, and InventoryDbContext's
            // InitialInventorySchema / AddSourceTypeCheckConstraint / AddAmendmentReason /
            // AddEatSourceType / AddCookReason.
            migrationBuilder.Sql(@"
                ALTER TABLE catalog.locations
                    ADD CONSTRAINT ""CK_locations_type"" CHECK (location_type IN ('ambient','frozen'));

                ALTER TABLE catalog.products
                    ADD CONSTRAINT ck_products_no_self_parent
                    CHECK (parent_product_id IS NULL OR id <> parent_product_id);
            ");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_conversions_source",
                schema: "catalog",
                table: "product_conversions",
                sql: "source IN ('user_confirmed','ai_suggested')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_units_display_style",
                schema: "catalog",
                table: "units",
                sql: "display_style IN ('decimal','fraction')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_units_unit_system",
                schema: "catalog",
                table: "units",
                sql: "unit_system IN ('unspecified','metric','us_customary')");

            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_reason
                    CHECK (reason IN ('Purchase','Consumed','Discarded','Correction','Amendment','Cook'));

                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_source_type
                    CHECK (source_type IS NULL OR source_type IN ('Manual','Intake','Cook','Eat'));
            ");

            // ADR-022 amendment (plantry-pcfe): unique EXPRESSION index over the unordered unit pair.
            // EF's fluent API cannot express an expression index, so this is raw SQL — PantryDbContext
            // and PantryDbContextModelSnapshot deliberately do NOT represent it (see the comment on
            // the ProductConversion entity configuration in PantryDbContext.OnModelCreating). Carried
            // over unchanged from CatalogDbContext's AddProductConversionUnorderedPairUniqueIndex.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_product_conversions_product_unordered_pair " +
                "ON catalog.product_conversions (product_id, LEAST(from_unit_id, to_unit_id), GREATEST(from_unit_id, to_unit_id));");

            // Non-superuser application role: RLS never applies to superusers (FORCE included), so the
            // app must connect as a regular role for the RLS backstop to mean anything. Idempotent
            // guard: Plantry.Identity.Infrastructure's baseline (which MigrationTargets.All always runs
            // first) already creates this role, but each schema's own baseline creates it defensively
            // too (mirrors InitialCatalogSchema / InitialMarketSchema), so this migration never depends
            // on migrator run order beyond Identity-first.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'app_user') THEN
                        CREATE ROLE app_user LOGIN PASSWORD 'app_user_password' NOSUPERUSER NOBYPASSRLS;
                    END IF;
                END
                $$;
            ");

            // RLS policies — backstop: even if the app-layer query filter is bypassed, Postgres
            // enforces per-household isolation for every catalog/inventory table. NULLIF(...,'')
            // treats an unset OR empty app.household_id as 'no tenant' (NULL, no rows visible) rather
            // than an invalid-uuid cast error.
            migrationBuilder.Sql(@"
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

                ALTER TABLE catalog.stores ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.stores FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON catalog.stores
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

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

                GRANT USAGE ON SCHEMA catalog TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA catalog TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA catalog TO app_user;

                ALTER TABLE inventory.product_stock ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.product_stock FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.product_stock
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE inventory.stock_entry ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.stock_entry FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.stock_entry
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE inventory.stock_journal_entry ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.stock_journal_entry FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.stock_journal_entry
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                ALTER TABLE inventory.household_inventory_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.household_inventory_settings FORCE ROW LEVEL SECURITY;
                CREATE POLICY household_isolation ON inventory.household_inventory_settings
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);

                GRANT USAGE ON SCHEMA inventory TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inventory TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA inventory TO app_user;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry DROP CONSTRAINT IF EXISTS fk_stock_journal_entry_stock_entry;
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ""FK_stock_journal_entry_stock_entry_entry_id""
                    FOREIGN KEY (entry_id) REFERENCES inventory.stock_entry (entry_id);

                ALTER TABLE inventory.stock_entry DROP CONSTRAINT IF EXISTS uq_stock_entry_household_entry;

                ALTER TABLE catalog.product_skus DROP CONSTRAINT IF EXISTS ""FK_product_skus_products_household_id_product_id"";
                ALTER TABLE catalog.product_skus
                    ADD CONSTRAINT ""FK_product_skus_products_product_id""
                    FOREIGN KEY (product_id) REFERENCES catalog.products (id) ON DELETE CASCADE;

                ALTER TABLE catalog.product_conversions DROP CONSTRAINT IF EXISTS ""FK_product_conversions_products_household_id_product_id"";
                ALTER TABLE catalog.product_conversions
                    ADD CONSTRAINT ""FK_product_conversions_products_product_id""
                    FOREIGN KEY (product_id) REFERENCES catalog.products (id) ON DELETE CASCADE;

                ALTER TABLE catalog.products DROP CONSTRAINT IF EXISTS ""FK_products_products_household_id_parent_product_id"";
                ALTER TABLE catalog.products DROP CONSTRAINT IF EXISTS ""AK_products_household_id_id"";
            ");

            migrationBuilder.Sql(@"
                REVOKE ALL ON ALL TABLES IN SCHEMA inventory FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA inventory FROM app_user;
                REVOKE USAGE ON SCHEMA inventory FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON inventory.household_inventory_settings;
                DROP POLICY IF EXISTS household_isolation ON inventory.stock_journal_entry;
                DROP POLICY IF EXISTS household_isolation ON inventory.stock_entry;
                DROP POLICY IF EXISTS household_isolation ON inventory.product_stock;

                REVOKE ALL ON ALL TABLES IN SCHEMA catalog FROM app_user;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA catalog FROM app_user;
                REVOKE USAGE ON SCHEMA catalog FROM app_user;
                DROP POLICY IF EXISTS household_isolation ON catalog.product_conversions;
                DROP POLICY IF EXISTS household_isolation ON catalog.product_skus;
                DROP POLICY IF EXISTS household_isolation ON catalog.products;
                DROP POLICY IF EXISTS household_isolation ON catalog.stores;
                DROP POLICY IF EXISTS household_isolation ON catalog.locations;
                DROP POLICY IF EXISTS household_isolation ON catalog.categories;
                DROP POLICY IF EXISTS household_isolation ON catalog.units;
            ");

            migrationBuilder.Sql("DROP INDEX IF EXISTS catalog.ix_product_conversions_product_unordered_pair;");

            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry DROP CONSTRAINT IF EXISTS ck_stock_journal_entry_source_type;
                ALTER TABLE inventory.stock_journal_entry DROP CONSTRAINT IF EXISTS ck_stock_journal_entry_reason;
            ");

            migrationBuilder.DropCheckConstraint(
                name: "ck_units_unit_system",
                schema: "catalog",
                table: "units");

            migrationBuilder.DropCheckConstraint(
                name: "ck_units_display_style",
                schema: "catalog",
                table: "units");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_conversions_source",
                schema: "catalog",
                table: "product_conversions");

            migrationBuilder.Sql(@"
                ALTER TABLE catalog.products DROP CONSTRAINT IF EXISTS ck_products_no_self_parent;
                ALTER TABLE catalog.locations DROP CONSTRAINT IF EXISTS ""CK_locations_type"";
            ");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "household_inventory_settings",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "locations",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_conversions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_skus",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "stock_journal_entry",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stores",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "units",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "stock_entry",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "product_stock",
                schema: "inventory");
        }
    }
}
