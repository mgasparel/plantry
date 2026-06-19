#!/usr/bin/env bash
# deploy/backup.sh — Plantry production database backup
#
# Runs pg_dump inside the running postgres container, writes a timestamped
# .dump file to BACKUP_DIR on the host, prunes files older than KEEP_DAYS,
# then optionally syncs the directory off-host via rclone.
#
# USAGE (manual):
#   POSTGRES_USER=plantry_owner POSTGRES_PASSWORD=secret ./deploy/backup.sh
#
# USAGE (cron — add to crontab on the production host):
#   0 3 * * *  POSTGRES_USER=plantry_owner POSTGRES_PASSWORD=secret \
#              RCLONE_REMOTE=backblaze:plantry-backups \
#              /home/plantry/plantry/deploy/backup.sh >> /var/log/plantry-backup.log 2>&1
#
# REQUIRED environment variables (can also be sourced from ~/plantry/.env):
#   POSTGRES_USER       — database owner credential (same user the migrator uses)
#   POSTGRES_PASSWORD   — database owner password
#
# OPTIONAL environment variables:
#   BACKUP_DIR          — host directory to store dumps (default: ~/plantry/backups)
#   COMPOSE_DIR         — directory containing docker-compose.prod.yml (default: ~/plantry)
#   COMPOSE_FILE        — override the compose file path entirely
#   POSTGRES_CONTAINER  — docker compose service name for postgres (default: postgres)
#   DB_NAME             — database name (default: plantrydb)
#   KEEP_DAYS           — days of backups to retain locally (default: 14)
#   RCLONE_REMOTE       — rclone remote:path for off-host sync; leave unset to skip sync
#                         Examples: "backblaze:plantry-backups"
#                                   "s3:my-bucket/plantry-backups"
#                                   "sftp:backup-host/plantry"
#   RCLONE_FLAGS        — extra flags passed to rclone (default: --transfers=4)
#
# SETUP (first run):
#   1. Install rclone on the host:  https://rclone.org/install/
#   2. Configure a remote:          rclone config
#   3. Test connectivity:           rclone ls <remote>:
#   4. Set RCLONE_REMOTE in the env or crontab line.
#
# EXIT CODES:
#   0  — dump succeeded and (if RCLONE_REMOTE set) sync succeeded
#   1  — dump failed (file is removed; local backups are intact)
#   2  — sync failed (dump and local backups are intact; alert but not fatal)
#
# NOTE: The script auto-sources ~/plantry/.env if it exists and the required
# variables are not already set, so the crontab line can be simple.

set -euo pipefail

# ── Defaults ─────────────────────────────────────────────────────────────────
COMPOSE_DIR="${COMPOSE_DIR:-$HOME/plantry}"
COMPOSE_FILE="${COMPOSE_FILE:-$COMPOSE_DIR/docker-compose.prod.yml}"
BACKUP_DIR="${BACKUP_DIR:-$COMPOSE_DIR/backups}"
POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-postgres}"
DB_NAME="${DB_NAME:-plantrydb}"
KEEP_DAYS="${KEEP_DAYS:-14}"
RCLONE_FLAGS="${RCLONE_FLAGS:---transfers=4}"

# ── Auto-source .env if needed ────────────────────────────────────────────────
ENV_FILE="$COMPOSE_DIR/.env"
if { [ -z "${POSTGRES_USER:-}" ] || [ -z "${POSTGRES_PASSWORD:-}" ]; } \
   && [ -f "$ENV_FILE" ]; then
  # shellcheck source=/dev/null
  set -a; source "$ENV_FILE"; set +a
fi

# ── Validate required vars ────────────────────────────────────────────────────
if [ -z "${POSTGRES_USER:-}" ]; then
  echo "ERROR: POSTGRES_USER is not set" >&2
  exit 1
fi
if [ -z "${POSTGRES_PASSWORD:-}" ]; then
  echo "ERROR: POSTGRES_PASSWORD is not set" >&2
  exit 1
fi

# ── Prepare output directory ──────────────────────────────────────────────────
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

TIMESTAMP=$(date -u +"%Y%m%dT%H%M%SZ")
DUMP_FILE="$BACKUP_DIR/plantrydb-${TIMESTAMP}.dump"

echo "==> Plantry DB backup started at $(date -u +"%Y-%m-%d %H:%M:%S UTC")"
echo "    DUMP_FILE : $DUMP_FILE"
echo "    KEEP_DAYS : $KEEP_DAYS"
echo "    DB        : $DB_NAME"

# ── pg_dump via docker compose exec ──────────────────────────────────────────
# Uses the owner credential (same as the migrator) so pg_dump can read all
# schemas including those owned by plantry_owner.  pg_dump writes the custom
# (-Fc) format — smaller than plain SQL, and restoreable with pg_restore
# including selective table/schema restores.  Piping via cat avoids writing to
# the container filesystem.
#
# If docker compose is not available, fall back to plain `docker exec`.
run_pg_dump() {
  if docker compose -f "$COMPOSE_FILE" version &>/dev/null 2>&1; then
    docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_CONTAINER" \
      env PGPASSWORD="$POSTGRES_PASSWORD" \
      pg_dump -U "$POSTGRES_USER" -d "$DB_NAME" -Fc --no-password
  else
    # Fallback: plain docker exec using the container name directly.
    # The container name follows compose's default naming: <project>-<service>-1.
    CONTAINER_NAME=$(docker ps --filter "label=com.docker.compose.service=$POSTGRES_CONTAINER" \
                               --format "{{.Names}}" | head -1)
    if [ -z "$CONTAINER_NAME" ]; then
      echo "ERROR: could not find a running $POSTGRES_CONTAINER container" >&2
      exit 1
    fi
    docker exec -i "$CONTAINER_NAME" \
      env PGPASSWORD="$POSTGRES_PASSWORD" \
      pg_dump -U "$POSTGRES_USER" -d "$DB_NAME" -Fc --no-password
  fi
}

# Run the dump; on failure remove the (empty) output file and exit 1.
echo "==> Running pg_dump..."
if run_pg_dump > "$DUMP_FILE"; then
  DUMP_SIZE=$(du -sh "$DUMP_FILE" | cut -f1)
  echo "    OK — $DUMP_SIZE written to $DUMP_FILE"
else
  echo "ERROR: pg_dump failed — removing incomplete file" >&2
  rm -f "$DUMP_FILE"
  exit 1
fi

# ── Prune old local backups ───────────────────────────────────────────────────
echo "==> Pruning backups older than $KEEP_DAYS days..."
find "$BACKUP_DIR" -name "plantrydb-*.dump" -mtime +"$KEEP_DAYS" -print -delete

# ── Off-host sync via rclone ──────────────────────────────────────────────────
if [ -n "${RCLONE_REMOTE:-}" ]; then
  echo "==> Syncing to $RCLONE_REMOTE..."
  # rclone sync mirrors the local directory to the remote, deleting remote files
  # that no longer exist locally (i.e. pruned old backups are also removed remotely).
  # --no-traverse is efficient when the remote is large; --checksum is skipped on
  # purpose because timestamps are reliable for backup files.
  if rclone sync "$BACKUP_DIR" "$RCLONE_REMOTE" $RCLONE_FLAGS --stats=0; then
    echo "    OK — synced to $RCLONE_REMOTE"
  else
    echo "WARNING: rclone sync failed — dump is safe locally but NOT off-host" >&2
    echo "         Check rclone config with: rclone ls $RCLONE_REMOTE" >&2
    exit 2
  fi
else
  echo "==> RCLONE_REMOTE not set — skipping off-host sync"
  echo "    Set RCLONE_REMOTE (e.g. 'backblaze:plantry-backups') to enable."
fi

echo "==> Backup complete at $(date -u +"%Y-%m-%d %H:%M:%S UTC")"
