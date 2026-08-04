-- deploy/reconcile-market-dbcontext.sql
--
-- One-time reconciliation for plantry-g3da.7: PricingDbContext + DealsDbContext were unified into a
-- single MarketDbContext with one squashed baseline migration
-- (Plantry.Market.Infrastructure/Migrations/Market/20260804030207_InitialMarketSchema.cs). A FRESH
-- database applies that migration normally via the migrator (EnsureSchema/CreateTable/RLS/GRANT — it
-- creates real objects because none exist yet). An ALREADY-DEPLOYED database already has the
-- pricing.* and deals.* schemas and data from the two former contexts' own migration histories, so
-- applying the baseline's Up() as a normal migration would try to re-create tables that already
-- exist and fail.
--
-- Run this ONCE, with owner credentials, against an existing homelab/production database BEFORE
-- deploying the build that ships MarketDbContext (i.e. before the next `migrator` run). It leaves the
-- schema and data untouched — it only reconciles EF's own migrations bookkeeping so the migrator
-- recognizes the baseline as already applied and skips re-running it.
--
-- See docs/Operations/deployment.md "One-time migration reconciliations" for the full runbook
-- (backup-first, verification steps).
--
-- Re-run safe: the guard above short-circuits once reconciliation has happened, so a second run
-- never touches history rows written by later Market migrations.

BEGIN;

-- 0. Precondition guard. This script deletes both old histories, so it must refuse to run
--    against a database that is not at the tip of BOTH former contexts (a behind database
--    would be silently stamped as "at baseline" with a schema that is missing columns).
DO $$
BEGIN
    IF to_regclass('deals."__EFMigrationsHistory"') IS NULL THEN
        IF EXISTS (SELECT 1 FROM pricing."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260804030207_InitialMarketSchema') THEN
            RAISE NOTICE 'Already reconciled — steps 1-3 are a no-op.';
            RETURN;
        END IF;
        RAISE EXCEPTION 'deals."__EFMigrationsHistory" is missing but the Market baseline is not recorded — refusing to run against an unrecognised database state.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pricing."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260727062544_RelabelPackAndDozenUnitReferences') THEN
        RAISE EXCEPTION 'pricing."__EFMigrationsHistory" is not at its final migration (20260727062544_RelabelPackAndDozenUnitReferences) — run the PREVIOUS build''s migrator to completion before reconciling.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM deals."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260730012042_AddStoreSubscriptionLastNewContentAt') THEN
        RAISE EXCEPTION 'deals."__EFMigrationsHistory" is not at its final migration (20260730012042_AddStoreSubscriptionLastNewContentAt) — run the PREVIOUS build''s migrator to completion before reconciling.';
    END IF;
END $$;

-- 1. Clear PricingDbContext's old row history. MarketDbContext reuses the history table's LOCATION
--    (the pricing schema, its EF default schema) but not the old row-set — after this, exactly one
--    row represents "the Market baseline has been applied", not the five-plus incremental migrations
--    that got the old PricingDbContext here.
DELETE FROM pricing."__EFMigrationsHistory" WHERE to_regclass('deals."__EFMigrationsHistory"') IS NOT NULL;

-- 2. Insert the baseline row marking MarketDbContext's squashed migration as already applied, so
--    db.Database.MigrateAsync() (Plantry.Migrator, and PostgresFixture in tests) treats it as a no-op
--    rather than trying to re-create the pricing.price_observation / deals.* tables.
INSERT INTO pricing."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260804030207_InitialMarketSchema', '10.0.8')
ON CONFLICT ("MigrationId") DO NOTHING;

-- 3. Retire DealsDbContext's now-unused history table entirely — MarketDbContext's history lives
--    solely in pricing.__EFMigrationsHistory going forward. The deals.* DATA tables (store_subscription,
--    flyer_import, deal, deal_match_memory) are untouched by this script.
DROP TABLE IF EXISTS deals."__EFMigrationsHistory";

COMMIT;
