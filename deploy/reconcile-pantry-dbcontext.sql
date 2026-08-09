-- deploy/reconcile-pantry-dbcontext.sql
--
-- One-time reconciliation for plantry-g3da.10: CatalogDbContext + InventoryDbContext were unified
-- into a single PantryDbContext with one squashed baseline migration
-- (Plantry.Pantry.Infrastructure/Migrations/Pantry/20260808165152_InitialPantrySchema.cs). A FRESH
-- database applies that migration normally via the migrator (EnsureSchema/CreateTable/RLS/GRANT — it
-- creates real objects because none exist yet). An ALREADY-DEPLOYED database already has the
-- catalog.* and inventory.* schemas and data from the two former contexts' own migration histories,
-- so applying the baseline's Up() as a normal migration would try to re-create tables that already
-- exist and fail.
--
-- Run this ONCE, with owner credentials, against an existing homelab/production database BEFORE
-- deploying the build that ships PantryDbContext (i.e. before the next `migrator` run). It leaves the
-- schema and data untouched — it only reconciles EF's own migrations bookkeeping so the migrator
-- recognizes the baseline as already applied and skips re-running it.
--
-- See docs/Operations/deployment.md "One-time migration reconciliations" for the full runbook
-- (backup-first, verification steps).
--
-- Re-run safe: the guard above short-circuits once reconciliation has happened, so a second run
-- never touches history rows written by later Pantry migrations.

BEGIN;

-- 0. Precondition guard. This script deletes both old histories, so it must refuse to run
--    against a database that is not at the tip of BOTH former contexts (a behind database
--    would be silently stamped as "at baseline" with a schema that is missing columns).
DO $$
BEGIN
    IF to_regclass('inventory."__EFMigrationsHistory"') IS NULL THEN
        IF EXISTS (SELECT 1 FROM catalog."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260808165152_InitialPantrySchema') THEN
            RAISE NOTICE 'Already reconciled — steps 1-3 are a no-op.';
            RETURN;
        END IF;
        RAISE EXCEPTION 'inventory."__EFMigrationsHistory" is missing but the Pantry baseline is not recorded — refusing to run against an unrecognised database state.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM catalog."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260808022111_AddLocationLastCountedAt') THEN
        RAISE EXCEPTION 'catalog."__EFMigrationsHistory" is not at its final migration (20260808022111_AddLocationLastCountedAt) — run the PREVIOUS build''s migrator to completion before reconciling.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM inventory."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260808035611_AddHouseholdInventorySettingsDefaultLocation') THEN
        RAISE EXCEPTION 'inventory."__EFMigrationsHistory" is not at its final migration (20260808035611_AddHouseholdInventorySettingsDefaultLocation) — run the PREVIOUS build''s migrator to completion before reconciling.';
    END IF;
END $$;

-- 1. Clear CatalogDbContext's old row history. PantryDbContext reuses the history table's LOCATION
--    (the catalog schema, its EF default schema) but not the old row-set — after this, exactly one
--    row represents "the Pantry baseline has been applied", not the many incremental migrations that
--    got the old CatalogDbContext here.
DELETE FROM catalog."__EFMigrationsHistory" WHERE to_regclass('inventory."__EFMigrationsHistory"') IS NOT NULL;

-- 2. Insert the baseline row marking PantryDbContext's squashed migration as already applied, so
--    db.Database.MigrateAsync() (Plantry.Migrator, and PostgresFixture in tests) treats it as a no-op
--    rather than trying to re-create the catalog.units / inventory.product_stock tables.
INSERT INTO catalog."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260808165152_InitialPantrySchema', '10.0.8')
ON CONFLICT ("MigrationId") DO NOTHING;

-- 3. Retire InventoryDbContext's now-unused history table entirely — PantryDbContext's history lives
--    solely in catalog.__EFMigrationsHistory going forward. The inventory.* DATA tables (product_stock,
--    stock_entry, stock_journal_entry, household_inventory_settings) are untouched by this script.
DROP TABLE IF EXISTS inventory."__EFMigrationsHistory";

COMMIT;
