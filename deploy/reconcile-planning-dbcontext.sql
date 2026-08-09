-- deploy/reconcile-planning-dbcontext.sql
--
-- One-time reconciliation for plantry-g3da.8: ShoppingDbContext + MealPlanningDbContext were unified
-- into a single PlanningDbContext with one squashed baseline migration
-- (Plantry.Planning.Infrastructure/Migrations/Planning/20260808180000_InitialPlanningSchema.cs). A
-- FRESH database applies that migration normally via the migrator (EnsureSchema/CreateTable/RLS/GRANT
-- — it creates real objects because none exist yet). An ALREADY-DEPLOYED database already has the
-- shopping.* and meal_planning.* schemas and data from the two former contexts' own migration
-- histories, so applying the baseline's Up() as a normal migration would try to re-create tables that
-- already exist and fail.
--
-- Run this ONCE, with owner credentials, against an existing homelab/production database BEFORE
-- deploying the build that ships PlanningDbContext (i.e. before the next `migrator` run). It leaves
-- the schema and data untouched — it only reconciles EF's own migrations bookkeeping so the migrator
-- recognizes the baseline as already applied and skips re-running it.
--
-- See docs/Operations/deployment.md "One-time migration reconciliations" for the full runbook
-- (backup-first, verification steps).
--
-- Re-run safe: the guard above short-circuits once reconciliation has happened, so a second run
-- never touches history rows written by later Planning migrations.

BEGIN;

-- 0. Precondition guard. This script deletes both old histories, so it must refuse to run
--    against a database that is not at the tip of BOTH former contexts (a behind database
--    would be silently stamped as "at baseline" with a schema that is missing columns).
DO $$
BEGIN
    IF to_regclass('meal_planning."__EFMigrationsHistory"') IS NULL THEN
        IF EXISTS (SELECT 1 FROM shopping."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260808180000_InitialPlanningSchema') THEN
            RAISE NOTICE 'Already reconciled — steps 1-3 are a no-op.';
            RETURN;
        END IF;
        RAISE EXCEPTION 'meal_planning."__EFMigrationsHistory" is missing but the Planning baseline is not recorded — refusing to run against an unrecognised database state.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM shopping."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260727062611_RelabelPackAndDozenUnitReferences') THEN
        RAISE EXCEPTION 'shopping."__EFMigrationsHistory" is not at its final migration (20260727062611_RelabelPackAndDozenUnitReferences) — run the PREVIOUS build''s migrator to completion before reconciling.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM meal_planning."__EFMigrationsHistory"
                   WHERE "MigrationId" = '20260801090000_ProductDishQuantitySnapshot') THEN
        RAISE EXCEPTION 'meal_planning."__EFMigrationsHistory" is not at its final migration (20260801090000_ProductDishQuantitySnapshot) — run the PREVIOUS build''s migrator to completion before reconciling.';
    END IF;
END $$;

-- 1. Clear ShoppingDbContext's old row history. PlanningDbContext reuses the history table's LOCATION
--    (the shopping schema, its EF default schema) but not the old row-set — after this, exactly one
--    row represents "the Planning baseline has been applied", not the four-plus incremental migrations
--    that got the old ShoppingDbContext here.
DELETE FROM shopping."__EFMigrationsHistory" WHERE to_regclass('meal_planning."__EFMigrationsHistory"') IS NOT NULL;

-- 2. Insert the baseline row marking PlanningDbContext's squashed migration as already applied, so
--    db.Database.MigrateAsync() (Plantry.Migrator, and PostgresFixture in tests) treats it as a no-op
--    rather than trying to re-create the shopping.shopping_list / meal_planning.* tables.
INSERT INTO shopping."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260808180000_InitialPlanningSchema', '10.0.8')
ON CONFLICT ("MigrationId") DO NOTHING;

-- 3. Retire MealPlanningDbContext's now-unused history table entirely — PlanningDbContext's history
--    lives solely in shopping.__EFMigrationsHistory going forward. The meal_planning.* DATA tables
--    (meal_plan, planned_meal, planned_dish, meal_slot_config, meal_slot, user_preference, tag_stance,
--    household_planning_settings, week_planning_override) are untouched by this script.
DROP TABLE IF EXISTS meal_planning."__EFMigrationsHistory";

COMMIT;
