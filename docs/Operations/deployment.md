# Deployment runbook — production (single host + Docker Compose)

> **How** Plantry is deployed and operated in production. The **why** lives in
> [ADR-016](../ADRs/ADR-016.md) (pipeline) and [ADR-017](../ADRs/ADR-017.md) (migrations);
> the runtime shape in [ADR-012](../ADRs/ADR-012.md). For third-party self-hosting see
> [self-hosting.md](self-hosting.md). For the rollout history/context, see
> [cicd-rollout-plan.md](cicd-rollout-plan.md).

Production is one Linux host running `docker compose`. There is no Kubernetes, no
managed control plane, and no Aspire AppHost at runtime — the AppHost is a
local-development tool only.

## Topology

```
┌─ docker compose on the host ───────────────────────────────────────────┐
│  caddy        reverse proxy, automatic TLS                       :443  │
│     └─▶ plantry-web   (image from GHCR, app_user creds)               │
│            └─▶ postgres   (data in a named volume)                     │
│  migrator     one-shot; runs on deploy, then exits                     │
│  backup       scheduled pg_dump sidecar → ./backups/ (bind-mount)      │
└────────────────────────────────────────────────────────────────────────┘
```

- **plantry-web** — the app. Connects to Postgres as the least-privilege `app_user`
  role (never the owner) so RLS applies. Holds the AI API key (optional).
- **migrator** — applies EF migrations with the owner connection and reconciles the
  `app_user` password from config, then exits (ADR-017). Gates web via
  `depends_on: service_completed_successfully`.
- **postgres** — system of record; data on a named volume.
- **caddy** — TLS termination + reverse proxy. Alternatives: Traefik, nginx.
- **backup** — sidecar container (postgres:17-alpine) that runs `pg_dump` on a cron
  schedule and writes custom-format dump files to `./backups/` on the host.

## Prerequisites (one-time)

- A Linux host with Docker Engine + the Compose plugin.
- DNS A/AAAA record pointing at the host (for Caddy's automatic TLS).
- To let CI run the deploy (release.yml stage 3): the `DEPLOY_ENABLED` repo
  variable set to `true`, plus the `DEPLOY_*` secrets (SSH key, host, user,
  domain). Leave `DEPLOY_ENABLED` unset to deploy manually / pull-based.
- The host logged in to GHCR only if images are private (`docker login ghcr.io`);
  the public `mgasparel/plantry/*` images need no login.

## Secrets / configuration

Runtime secrets live in a root-owned `.env` file (mode `600`) next to the Compose
file, referenced by `docker-compose.prod.yml`. Three matter for the app; two more
for backups:

| Variable | Used by | Notes |
|---|---|---|
| `POSTGRES_PASSWORD` | postgres, migrator, backup | Database **owner**. Postgres only applies it on **first** volume init — pin it once and keep it stable. |
| `APP_USER_PASSWORD` | migrator, plantry-web | The least-privilege runtime role. The migrator `ALTER ROLE`s `app_user` to this on every run (ADR-017); the web app connects with it. Rotatable: change here, re-run the migrator. |
| AI API key | plantry-web | Optional. Absent → the receipt/plan AI degrades to a locked-feature UI, the app still runs. |
| `BACKUP_CRON` | backup | Cron schedule for pg_dump (default: `0 3 * * *` = 03:00 UTC daily). |
| `KEEP_DAYS` | backup | Days of local dump files to retain (default: `14`). |
| `BACKUP_DIR` | backup | Host path for the dump bind-mount (default: `./backups` next to compose file). |
| `RCLONE_REMOTE` | deploy/backup.sh | rclone remote for off-host sync (e.g. `backblaze:plantry-backups`). Not set in the container; consumed by the host-side script. |

CI→deploy secrets (SSH key, registry credentials) live in GitHub Actions
repository secrets / environments — never in the repo.

## Deploy procedure

Production deploys are **release-gated**, not per-merge. Pushing a semver tag
(`git push origin vX.Y.Z`) runs `release.yml`, which: verifies the full suite,
builds + pushes the semver image family to GHCR, and — only when the repository
variable `DEPLOY_ENABLED` is `true` — SSHes to the host and rolls it. With
`DEPLOY_ENABLED` unset, the SSH deploy is skipped (the model for pull-based
hosts, which pull the image themselves). `release.yml` is the authoritative
sequence.

```bash
# CI (release.yml) on `git push origin vX.Y.Z`:
#   verify → build + push ghcr.io/mgasparel/plantry/{web,migrator}:X.Y.Z (+ :X.Y :X :latest)
#   → (if vars.DEPLOY_ENABLED == 'true') the ssh deploy below
```

The deploy stage's exact manual equivalent, run from the compose directory on
the host (`VERSION` is the tag without the leading `v`, e.g. `1.4.0`):

```bash
export VERSION=1.4.0
export PLANTRY_WEB_IMAGE=ghcr.io/mgasparel/plantry/web:$VERSION
export MIGRATOR_IMAGE=ghcr.io/mgasparel/plantry/migrator:$VERSION

# 1. pull the exact released images
docker compose -f docker-compose.prod.yml pull plantry-web migrator

# 2. migrations — explicit, fails loud, blocks the deploy (ADR-017)
docker compose -f docker-compose.prod.yml run --rm migrator

# 3. roll the web service (and caddy, so Caddyfile changes take effect)
docker compose -f docker-compose.prod.yml up -d plantry-web caddy

# 4. smoke checks — /alive (liveness) then /ready (DB connectivity)
curl -fsS https://<host>/alive
curl -fsS https://<host>/ready
```

If step 2 exits non-zero, **stop** — the schema is unchanged and the old web
container is still serving. Investigate before proceeding.

## Health checks

Three endpoints are mapped by `MapDefaultEndpoints` (`ServiceDefaults/Extensions.cs`):

| Endpoint | Tags | Environment | Purpose | Body |
|---|---|---|---|---|
| `/alive` | `live` | All (unconditional) | Liveness — self-check, always passes | `Healthy` |
| `/ready` | `ready` | All (unconditional) | Readiness — DB connectivity probe (`CanConnectAsync`) | `Healthy` or `Unhealthy` |
| `/health` | all | Development only | Full diagnostic detail with check names and durations | JSON |

**Container healthchecks target `/alive` (liveness), not `/ready` (readiness).** A transient DB
blip must not mark the web container unhealthy, trigger restart loops, or take Caddy down for
DB-independent pages (e.g. the landing and login pages). Readiness is informational.

**`/ready` is safe for public exposure** because the response writer emits only `Healthy` or
`Unhealthy` text — no check names, durations, or DB exception detail. Use it for:
- Post-deploy smoke checks (fails loudly if the new container cannot reach the DB).
- External uptime monitoring to alert on DB outages.
- The OTLP observability backend (ess9.6).

The post-deploy smoke step in `release.yml` checks both `/alive` (liveness) and `/ready` (DB
connectivity) so a deploy where the new container cannot reach the database fails loudly before
Caddy begins serving real traffic.

## Rollback

Forward-fix is preferred, but to revert the app: re-point `PLANTRY_WEB_IMAGE` at
the previous semver tag (e.g. `ghcr.io/mgasparel/plantry/web:1.3.2`) and
`docker compose up -d plantry-web`. **Schema does not roll
back** — EF down-migrations are not run in production. If a migration must be
undone, write a new forward migration that reverses it. This is why backups gate
any risky migration — take a fresh backup immediately before any deploy that
includes a destructive schema change.

---

## Backups

Plantry owns the host and therefore owns the backups. This is non-negotiable once
real data lands (ADR-017). The backup approach is two layers:

1. **Local scheduled dumps** — a `backup` sidecar container runs `pg_dump` on a
   cron schedule and stores compressed, custom-format (`.dump`) files in `./backups/`
   on the host (a bind-mount that survives container restarts).
2. **Off-host sync** — a host-level cron runs `deploy/backup.sh` which calls
   `rclone sync` to mirror `./backups/` to a cloud storage remote (Backblaze B2,
   S3, SFTP, etc.). This is the durable off-host copy.

### Architecture

```
postgres container
    └─▶ backup container (pg_dump -Fc)
              └─▶ ./backups/ on host (bind-mount, local retention)
                        └─▶ rclone (host cron, deploy/backup.sh)
                                  └─▶ off-host remote (Backblaze B2 / S3 / SFTP)
```

The `backup` container in `docker-compose.prod.yml` handles layer 1 automatically.
Layer 2 requires `rclone` to be installed on the host and `RCLONE_REMOTE` to be set.

### Setting up off-host sync (first time)

```bash
# 1. Install rclone on the host
curl https://rclone.org/install.sh | sudo bash

# 2. Configure a remote (interactive wizard)
rclone config
# Follow the prompts.  Common choices:
#   Backblaze B2:  type=b2, account=<keyId>, key=<appKey>
#   S3-compatible: type=s3, provider=<provider>, access_key_id=..., secret_access_key=...
#   SFTP:          type=sftp, host=<host>, user=<user>, key_file=<path>

# 3. Verify connectivity
rclone ls <remote>:   # should list the bucket/directory

# 4. Add RCLONE_REMOTE to .env
echo 'RCLONE_REMOTE=backblaze:plantry-backups' >> ~/plantry/.env

# 5. Add to the host crontab (runs after the container's internal dump, with overlap)
# This syncs whatever the container has written to ./backups/ off-host.
# The container's BACKUP_CRON defaults to 03:00 UTC; the host cron runs at 03:15
# to ensure the dump has completed before the sync starts.
(crontab -l 2>/dev/null; echo '15 3 * * * cd ~/plantry && RCLONE_REMOTE=backblaze:plantry-backups ./deploy/backup.sh >> /var/log/plantry-backup.log 2>&1') | crontab -
```

The `deploy/backup.sh` script can also be used standalone (it invokes `pg_dump`
itself via `docker compose exec`). This is useful for ad-hoc backups or when the
sidecar container is not running:

```bash
cd ~/plantry
POSTGRES_USER=plantry_owner POSTGRES_PASSWORD=<secret> \
RCLONE_REMOTE=backblaze:plantry-backups \
./deploy/backup.sh
```

### What the backup contains

- **Format:** PostgreSQL custom format (`-Fc`) — compressed, supports selective
  restore with `pg_restore`. Smaller than plain SQL.
- **Credentials used:** database owner (`POSTGRES_USER`) — the same role the
  migrator uses. The `app_user` runtime role cannot dump schema.
- **Retention:** files older than `KEEP_DAYS` (default 14) are pruned from
  `./backups/` both locally and on the remote (rclone sync mirrors the prune).
- **Naming:** `plantrydb-YYYYMMDDTHHMMSSZ.dump` (UTC timestamp).

### Pre-migration backup

Always take a manual backup immediately before a deploy that includes a destructive
schema change (e.g. dropping a column, changing a constraint):

```bash
cd ~/plantry

# Option A: trigger via the host script (also syncs off-host)
POSTGRES_USER=plantry_owner POSTGRES_PASSWORD=<secret> \
RCLONE_REMOTE=backblaze:plantry-backups \
./deploy/backup.sh

# Option B: manual pg_dump inside the running container
TIMESTAMP=$(date -u +"%Y%m%dT%H%M%SZ")
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<secret> \
  pg_dump -U plantry_owner -d plantrydb -Fc \
  > ./backups/plantrydb-premigration-${TIMESTAMP}.dump
```

---

## One-time migration reconciliations

Most schema changes are ordinary forward migrations the migrator applies automatically
(step 3 of the deploy procedure). A few land as a **squashed baseline** instead — a new
migration-owning DbContext replacing older ones and collapsing their migration history
into one file — which an already-deployed database cannot apply via the normal `migrator`
run (its `Up()` tries to `CREATE TABLE` objects that already exist). Those need a one-time
manual reconciliation, run once against the target database **before** the deploy that
ships the new build. Each one is listed here as it ships; none of these repeat.

### plantry-g3da.7 — Market DbContext unification (Pricing + Deals)

**What changed:** `PricingDbContext` and `DealsDbContext` (two DbContexts sharing
`Plantry.Market.Infrastructure`, each with its own migration history in its own schema)
are unified into one `MarketDbContext` with a single squashed baseline migration —
`Migrations/Market/20260804030207_InitialMarketSchema.cs`. The `pricing.*` and `deals.*`
schemas and their data are **untouched** (ADR-024 §"Physical schemas do not move on day
one") — only the EF migrations bookkeeping changes: MarketDbContext's history table lives
in `pricing.__EFMigrationsHistory` (its EF default schema); `deals.__EFMigrationsHistory`
is retired.

**Run once, with owner credentials, before deploying a build that ships MarketDbContext:**

```bash
cd ~/plantry

# 0. Take a backup first (see "Pre-migration backup" above) — this touches the
#    migrations bookkeeping table directly, outside the migrator's normal path.
POSTGRES_USER=plantry_owner POSTGRES_PASSWORD=<secret> ./deploy/backup.sh

# 1. Apply the reconciliation script (guarded — aborts loudly if the database is not at the tip
#    of both former histories; a no-op once already reconciled)
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -v ON_ERROR_STOP=1 \
  < deploy/reconcile-market-dbcontext.sql

# 2. Verify: exactly one row in pricing.__EFMigrationsHistory for the new baseline,
#    and deals.__EFMigrationsHistory is gone.
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -c \
    "SELECT \"MigrationId\" FROM pricing.\"__EFMigrationsHistory\";"
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -c \
    "SELECT to_regclass('deals.\"__EFMigrationsHistory\"');"  # expect NULL

# 3. Proceed with the normal deploy procedure (step 3, `migrator`, is then a no-op
#    for the Market baseline — it sees the row from step 1 as already applied).
```

If the reconciliation script has not been run and the migrator runs anyway, it fails
loud on the `CREATE TABLE ... already exists` error (ADR-017's fail-loud contract) — the
old web container keeps serving, no data is at risk. Re-run the reconciliation script and
retry the migrator.

### plantry-g3da.10 — Pantry DbContext unification (Catalog + Inventory)

**What changed:** `CatalogDbContext` and `InventoryDbContext` (two DbContexts sharing
`Plantry.Pantry.Infrastructure`, each with its own migration history in its own schema)
are unified into one `PantryDbContext` with a single squashed baseline migration —
`Migrations/Pantry/20260808165152_InitialPantrySchema.cs`. The `catalog.*` and
`inventory.*` schemas and their data are **untouched** (ADR-024 §"Physical schemas do not
move on day one") — only the EF migrations bookkeeping changes: PantryDbContext's history
table lives in `catalog.__EFMigrationsHistory` (its EF default schema);
`inventory.__EFMigrationsHistory` is retired.

**Run once, with owner credentials, before deploying a build that ships PantryDbContext:**

```bash
cd ~/plantry

# 0. Take a backup first (see "Pre-migration backup" above) — this touches the
#    migrations bookkeeping table directly, outside the migrator's normal path.
POSTGRES_USER=plantry_owner POSTGRES_PASSWORD=<secret> ./deploy/backup.sh

# 1. Apply the reconciliation script (guarded — aborts loudly if the database is not at the tip
#    of both former histories; a no-op once already reconciled)
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -v ON_ERROR_STOP=1 \
  < deploy/reconcile-pantry-dbcontext.sql

# 2. Verify: exactly one row in catalog.__EFMigrationsHistory for the new baseline,
#    and inventory.__EFMigrationsHistory is gone.
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -c \
    "SELECT \"MigrationId\" FROM catalog.\"__EFMigrationsHistory\";"
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -c \
    "SELECT to_regclass('inventory.\"__EFMigrationsHistory\"');"  # expect NULL

# 3. Proceed with the normal deploy procedure (step 3, `migrator`, is then a no-op
#    for the Pantry baseline — it sees the row from step 1 as already applied).
```

If the reconciliation script has not been run and the migrator runs anyway, it fails
loud on the `CREATE TABLE ... already exists` error (ADR-017's fail-loud contract) — the
old web container keeps serving, no data is at risk. Re-run the reconciliation script and
retry the migrator.

### plantry-g3da.8 — Planning DbContext unification (Meal Planning + Shopping)

**What changed:** `ShoppingDbContext` and `MealPlanningDbContext` (two DbContexts sharing
`Plantry.Planning.Infrastructure`, each with its own migration history in its own schema)
are unified into one `PlanningDbContext` with a single squashed baseline migration —
`Migrations/Planning/20260808180000_InitialPlanningSchema.cs`. The `shopping.*` and
`meal_planning.*` schemas and their data are **untouched** (ADR-024 §"Physical schemas do
not move on day one") — only the EF migrations bookkeeping changes: PlanningDbContext's
history table lives in `shopping.__EFMigrationsHistory` (its EF default schema);
`meal_planning.__EFMigrationsHistory` is retired.

**Run once, with owner credentials, before deploying a build that ships PlanningDbContext:**

```bash
cd ~/plantry

# 0. Take a backup first (see "Pre-migration backup" above) — this touches the
#    migrations bookkeeping table directly, outside the migrator's normal path.
POSTGRES_USER=plantry_owner POSTGRES_PASSWORD=<secret> ./deploy/backup.sh

# 1. Apply the reconciliation script (guarded — aborts loudly if the database is not at the tip
#    of both former histories; a no-op once already reconciled)
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -v ON_ERROR_STOP=1 \
  < deploy/reconcile-planning-dbcontext.sql

# 2. Verify: exactly one row in shopping.__EFMigrationsHistory for the new baseline,
#    and meal_planning.__EFMigrationsHistory is gone.
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -c \
    "SELECT \"MigrationId\" FROM shopping.\"__EFMigrationsHistory\";"
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<owner-password> \
  psql -U plantry_owner -d plantrydb -c \
    "SELECT to_regclass('meal_planning.\"__EFMigrationsHistory\"');"  # expect NULL

# 3. Proceed with the normal deploy procedure (step 3, `migrator`, is then a no-op
#    for the Planning baseline — it sees the row from step 1 as already applied).
```

If the reconciliation script has not been run and the migrator runs anyway, it fails
loud on the `CREATE TABLE ... already exists` error (ADR-017's fail-loud contract) — the
old web container keeps serving, no data is at risk. Re-run the reconciliation script and
retry the migrator.

---

## Restore procedure

The restore procedure is scripted in `deploy/restore.sh`. Run through it in a
staging environment before you need it in production.

### Quick reference

```bash
cd ~/plantry
./deploy/restore.sh ./backups/plantrydb-20260619T030000Z.dump
```

The script:
1. Stops `plantry-web` (prevents writes during restore).
2. Drops and recreates `plantrydb` (data loss — intentional).
3. Runs `pg_restore` from the dump file (piped into the container).
4. Re-runs the migrator to bring the schema current (idempotent if already current).
5. Restarts `plantry-web`.

### Full manual restore (if the script is unavailable)

```bash
# 0. Set credentials
PGOWNER=plantry_owner
PGPASS=<owner-password>
DUMP=/path/to/plantrydb-YYYYMMDDTHHMMSSZ.dump

cd ~/plantry

# 1. Stop the web app
docker compose -f docker-compose.prod.yml stop plantry-web

# 2. Verify postgres is healthy
docker compose -f docker-compose.prod.yml exec postgres \
  pg_isready -U "$PGOWNER" -d postgres

# 3. Terminate connections and recreate the database
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD="$PGPASS" \
  psql -U "$PGOWNER" -d postgres -c \
    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='plantrydb' AND pid<>pg_backend_pid();"

docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD="$PGPASS" \
  psql -U "$PGOWNER" -d postgres -c "DROP DATABASE IF EXISTS plantrydb;"

docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD="$PGPASS" \
  psql -U "$PGOWNER" -d postgres -c "CREATE DATABASE plantrydb OWNER \"$PGOWNER\";"

# 4. Restore from dump
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD="$PGPASS" \
  pg_restore -U "$PGOWNER" -d plantrydb -Fc -j 2 \
    --no-owner --no-privileges --exit-on-error \
  < "$DUMP"

# 5. Re-run migrator (brings schema to current version)
docker compose -f docker-compose.prod.yml run --rm migrator

# 6. Restart web app
docker compose -f docker-compose.prod.yml up -d plantry-web

# 7. Verify
curl -fsS https://<domain>/alive
```

### Testing the restore (periodic drill)

Run this drill on a staging host before you need it in production. The goal is to
confirm the backup file is valid and the procedure works end-to-end.

```bash
# On a staging host with the same compose stack:
cd ~/plantry-staging

# 1. Copy a recent backup from production (or from the off-host remote)
rclone copy backblaze:plantry-backups/plantrydb-20260619T030000Z.dump ./backups/

# 2. Run the restore script (it will warn before destroying data)
./deploy/restore.sh ./backups/plantrydb-20260619T030000Z.dump

# 3. Spot-check: confirm the data makes sense
docker compose -f docker-compose.prod.yml exec -T postgres \
  env PGPASSWORD=<staging-owner-password> \
  psql -U plantry_owner -d plantrydb -c "SELECT count(*) FROM households;"

# 4. Verify the app is healthy
curl -fsS https://<staging-domain>/alive
```

Perform this drill:
- Before the first production deployment with real user data.
- After any significant schema migration.
- At least quarterly once the app is in active use.

---

## Notes / divergences

- Production uses a hand-maintained `docker-compose.prod.yml`, **not** the
  Aspire-generated `aspire-output/docker-compose.yaml` — the generated file bundles
  the Aspire dashboard and passes the Postgres superuser password to the web app.
  See the ADR-012 amendment and [ADR-016](../ADRs/ADR-016.md).
