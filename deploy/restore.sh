#!/usr/bin/env bash
# deploy/restore.sh — Plantry production database restore
#
# Restores a pg_dump custom-format (.dump) file into the running postgres
# container.  This is the authoritative restore procedure — run through it in
# a staging environment before you need it in production.
#
# USAGE:
#   ./deploy/restore.sh /path/to/plantrydb-20260619T030000Z.dump
#
# REQUIRED arguments:
#   $1  — path to the .dump file produced by deploy/backup.sh
#
# REQUIRED environment variables (auto-sourced from ~/plantry/.env if set):
#   POSTGRES_USER       — database owner credential
#   POSTGRES_PASSWORD   — database owner password
#
# OPTIONAL environment variables:
#   COMPOSE_DIR              — directory containing docker-compose.prod.yml (default: ~/plantry)
#   COMPOSE_FILE             — override the compose file path entirely
#   POSTGRES_CONTAINER       — docker compose service name for postgres (default: postgres)
#   DB_NAME                  — database to restore into (default: plantrydb)
#   RESTORE_JOBS             — parallel restore workers passed to pg_restore -j (default: 2)
#   RESTORE_SKIP_CONFIRM     — set to 1 to skip the 10-second countdown (CI use)
#   RESTORE_SKIP_MIGRATOR    — set to 1 to skip the migrator re-run step (CI use;
#                              the migrator image may not be available outside prod)
#
# WHAT THIS SCRIPT DOES:
#   1. Stops the web app (and migrator if running) to prevent writes during restore.
#   2. Drops and recreates the target database (DATA LOSS — intentional on restore).
#   3. Runs pg_restore into the fresh database.
#   4. Re-runs the migrator to bring schema current (skipped if RESTORE_SKIP_MIGRATOR=1).
#   5. Restarts the web app.
#
# IMPORTANT — READ BEFORE RUNNING:
#   - This script DESTROYS the current database contents.  There is no undo.
#     Take a fresh pg_dump of the current state first if you want a safety net.
#   - Schema does NOT roll back (ADR-017).  If the backup predates a migration
#     that added NOT NULL columns, the migrator re-run after restore will replay
#     those migrations on the restored data — this is the intended flow.
#   - After restore, always re-run the migrator so the schema is current:
#       docker compose -f ~/plantry/docker-compose.prod.yml run --rm migrator
#   - The restore should be tested periodically in a staging environment.
#     See the "Testing the restore" section in docs/Operations/deployment.md.
#
# EXIT CODES:
#   0  — restore complete
#   1  — pre-flight check failed (no destructive action taken)
#   2  — restore failed (database may be in a partial state — investigate)

set -euo pipefail

# ── Pre-flight ────────────────────────────────────────────────────────────────
DUMP_FILE="${1:-}"
if [ -z "$DUMP_FILE" ]; then
  echo "Usage: $0 <path-to-dump-file>" >&2
  echo "  Example: $0 ~/plantry/backups/plantrydb-20260619T030000Z.dump" >&2
  exit 1
fi
if [ ! -f "$DUMP_FILE" ]; then
  echo "ERROR: dump file not found: $DUMP_FILE" >&2
  exit 1
fi

# ── Defaults ─────────────────────────────────────────────────────────────────
COMPOSE_DIR="${COMPOSE_DIR:-$HOME/plantry}"
COMPOSE_FILE="${COMPOSE_FILE:-$COMPOSE_DIR/docker-compose.prod.yml}"
POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-postgres}"
DB_NAME="${DB_NAME:-plantrydb}"
RESTORE_JOBS="${RESTORE_JOBS:-2}"
RESTORE_SKIP_CONFIRM="${RESTORE_SKIP_CONFIRM:-0}"
RESTORE_SKIP_MIGRATOR="${RESTORE_SKIP_MIGRATOR:-0}"

# ── Auto-source .env ──────────────────────────────────────────────────────────
ENV_FILE="$COMPOSE_DIR/.env"
if { [ -z "${POSTGRES_USER:-}" ] || [ -z "${POSTGRES_PASSWORD:-}" ]; } \
   && [ -f "$ENV_FILE" ]; then
  # shellcheck source=/dev/null
  set -a; source "$ENV_FILE"; set +a
fi

if [ -z "${POSTGRES_USER:-}" ]; then
  echo "ERROR: POSTGRES_USER is not set" >&2
  exit 1
fi
if [ -z "${POSTGRES_PASSWORD:-}" ]; then
  echo "ERROR: POSTGRES_PASSWORD is not set" >&2
  exit 1
fi

DUMP_SIZE=$(du -sh "$DUMP_FILE" | cut -f1)
echo "==> Plantry DB restore"
echo "    DUMP_FILE        : $DUMP_FILE ($DUMP_SIZE)"
echo "    DB               : $DB_NAME"
echo "    COMPOSE          : $COMPOSE_FILE"
echo "    SKIP_MIGRATOR    : $RESTORE_SKIP_MIGRATOR"
echo ""
echo "    WARNING: This will DESTROY the current '$DB_NAME' database."

if [ "$RESTORE_SKIP_CONFIRM" != "1" ]; then
  echo "    Press Ctrl-C within 10 seconds to abort..."
  sleep 10
fi

echo ""
echo "==> Proceeding with restore at $(date -u +"%Y-%m-%d %H:%M:%S UTC")"

# Helper: run a command inside the postgres container via compose exec.
pg_exec() {
  docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_CONTAINER" \
    env PGPASSWORD="$POSTGRES_PASSWORD" \
    "$@"
}

# ── Step 1: Stop the web app (prevent writes) ─────────────────────────────────
echo "==> Stopping plantry-web and migrator..."
docker compose -f "$COMPOSE_FILE" stop plantry-web migrator 2>/dev/null || true
echo "    OK — web app stopped"

# ── Step 2: Ensure postgres is healthy ───────────────────────────────────────
echo "==> Checking postgres health..."
for attempt in 1 2 3 4 5 6; do
  if docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_CONTAINER" \
       pg_isready -U "$POSTGRES_USER" -d postgres &>/dev/null; then
    echo "    OK — postgres is ready"
    break
  fi
  if [ "$attempt" -eq 6 ]; then
    echo "ERROR: postgres did not become healthy after 30s" >&2
    exit 2
  fi
  echo "    Waiting for postgres... ($attempt/6)"
  sleep 5
done

# ── Step 3: Drop and recreate the target database ────────────────────────────
# Connect to the 'postgres' maintenance database (not plantrydb itself) so we
# can drop plantrydb.  Force-terminate any stray connections first.
echo "==> Dropping existing '$DB_NAME'..."
pg_exec psql -U "$POSTGRES_USER" -d postgres -c \
  "SELECT pg_terminate_backend(pid)
     FROM pg_stat_activity
    WHERE datname = '$DB_NAME' AND pid <> pg_backend_pid();"
pg_exec psql -U "$POSTGRES_USER" -d postgres -c \
  "DROP DATABASE IF EXISTS \"$DB_NAME\";"
pg_exec psql -U "$POSTGRES_USER" -d postgres -c \
  "CREATE DATABASE \"$DB_NAME\" OWNER \"$POSTGRES_USER\";"
echo "    OK — '$DB_NAME' recreated"

# ── Step 4: Restore from dump ─────────────────────────────────────────────────
# pg_restore reads from stdin (piped from the dump file on the host).
# -Fc: custom format (matches pg_dump -Fc in backup.sh).
# -j:  parallel workers (2 is safe for a small server; increase on larger hosts).
# --no-owner: roles may differ on the restore target; schema ownership is set
#             by the migrator re-run in step 5.
# --no-privileges: ACLs are rebuilt by the migrator.
# --exit-on-error: fail fast on the first restore error rather than continuing
#                  with a partial restore.
echo "==> Running pg_restore (jobs=$RESTORE_JOBS)..."
if docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_CONTAINER" \
     env PGPASSWORD="$POSTGRES_PASSWORD" \
     pg_restore -U "$POSTGRES_USER" -d "$DB_NAME" \
       -Fc -j "$RESTORE_JOBS" \
       --no-owner --no-privileges \
       --exit-on-error \
     < "$DUMP_FILE"; then
  echo "    OK — data restored"
else
  echo "ERROR: pg_restore exited non-zero — database may be partial" >&2
  echo "       Investigate, then restore again from the same dump file." >&2
  exit 2
fi

# ── Step 5: Re-run the migrator ───────────────────────────────────────────────
# The restored database has the schema as of the dump date.  Running the
# migrator brings it up to the current application version (idempotent if
# already current).  This is required before the web app starts.
# Skip with RESTORE_SKIP_MIGRATOR=1 when the migrator image is not available
# (e.g. CI environments without a registry push).
if [ "$RESTORE_SKIP_MIGRATOR" = "1" ]; then
  echo "==> Skipping migrator (RESTORE_SKIP_MIGRATOR=1)"
  echo "    NOTE: In production, always run the migrator after a restore."
else
  echo "==> Re-running migrator to ensure schema is current..."
  if docker compose -f "$COMPOSE_FILE" run --rm migrator; then
    echo "    OK — schema is current"
  else
    echo "ERROR: migrator failed after restore — web app will not start" >&2
    echo "       Check migration logs; schema may need manual intervention." >&2
    exit 2
  fi
fi

# ── Step 6: Restart the web app ───────────────────────────────────────────────
echo "==> Restarting plantry-web..."
docker compose -f "$COMPOSE_FILE" up -d plantry-web 2>/dev/null || \
  echo "    NOTE: plantry-web could not start (expected in CI without full image set)"
echo "    OK"

echo ""
echo "==> Restore complete at $(date -u +"%Y-%m-%d %H:%M:%S UTC")"
echo "    Verify: curl -fsS https://<your-domain>/alive"
echo "    Check app logs: docker compose -f $COMPOSE_FILE logs -f plantry-web"
