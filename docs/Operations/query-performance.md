# Postgres query-performance investigation

This runbook covers slow-query diagnosis using `pg_stat_statements` and
`EXPLAIN (ANALYZE, BUFFERS)`.

## Prerequisites

`pg_stat_statements` is enabled automatically on every stack up:

- The postgres service in `docker-compose.yml` and `docker-compose.prod.yml`
  loads it via `shared_preload_libraries=pg_stat_statements` at startup.
- `Plantry.Migrator` runs `CREATE EXTENSION IF NOT EXISTS pg_stat_statements`
  on every deploy (idempotent), so the extension view is queryable immediately
  after `docker compose up -d` completes — no manual `psql` step required.

No operator action is needed on a fresh install or after a `docker compose pull && up -d`.

## Slow-query log

The postgres service logs any query that exceeds `PGCONFIG_LOG_MIN_DURATION_STATEMENT`
(default: **1000 ms**). Slow-query lines appear in the postgres container log:

```bash
docker compose logs postgres | grep "duration:"
# or stream in real time:
docker compose logs -f postgres | grep "duration:"
```

A typical slow-query log line looks like:

```
2026-06-27 03:14:15.123 UTC [42] LOG:  duration: 1204.321 ms  statement: SELECT ...
```

### Adjusting the threshold

To temporarily lower the threshold (e.g. to catch queries > 500 ms) without
a full restart, set the variable in `.env` and restart the postgres service:

```bash
# .env
PGCONFIG_LOG_MIN_DURATION_STATEMENT=500

docker compose restart postgres
# (or: docker compose up -d postgres — recreates the container with the new flag)
```

Set to `0` to log every query. This is high-volume on a busy instance — restore
the default (`1000`) after your investigation session.

## Finding the worst offenders with pg_stat_statements

Connect to the database as the owner role:

```bash
docker compose exec -it postgres \
  psql -U "${POSTGRES_USER}" -d plantrydb
```

Then query the extension view to rank queries by total time:

```sql
-- Top 20 queries by total execution time (all-time, since last reset).
SELECT
    calls,
    round(total_exec_time::numeric, 2)   AS total_ms,
    round(mean_exec_time::numeric,  2)   AS mean_ms,
    round(stddev_exec_time::numeric, 2)  AS stddev_ms,
    rows,
    left(query, 120)                     AS query_snippet
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 20;
```

Other useful cuts:

```sql
-- Queries with high mean latency (even if infrequently called).
SELECT calls, round(mean_exec_time::numeric, 2) AS mean_ms, left(query, 120) AS query_snippet
FROM pg_stat_statements
ORDER BY mean_exec_time DESC
LIMIT 20;

-- Queries returning the most rows (potential missing pagination or N+1 issues).
SELECT calls, rows, round(mean_exec_time::numeric, 2) AS mean_ms, left(query, 120) AS query_snippet
FROM pg_stat_statements
ORDER BY rows DESC
LIMIT 20;
```

Reset statistics when starting a fresh investigation (otherwise old numbers
from a previous session pollute the view):

```sql
SELECT pg_stat_statements_reset();
```

## Diagnosing a specific query with EXPLAIN

Once you have identified a slow query, run it with `EXPLAIN (ANALYZE, BUFFERS)`
to see the actual execution plan, row estimates, and buffer usage:

```sql
EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)
SELECT s.id, s.quantity, p.name
FROM inventory.stock_entries s
JOIN catalog.products p ON p.id = s.product_id
WHERE s.household_id = 'your-household-uuid'
  AND s.quantity > 0
ORDER BY s.expires_at;
```

Key things to look for in the plan output:

| Signal | What it means |
|--------|---------------|
| `Seq Scan` on a large table | A full table scan — likely a missing index. |
| `Rows Removed by Filter: N` is large relative to actual rows | The index (if any) is not selective enough. |
| `Buffers: shared hit=N read=M` — `read` is non-zero | Pages read from disk rather than cache; consider `pg_prewarm` or investigate cache pressure. |
| `Nested Loop` with many outer rows | Potential N+1 — consider a `Hash Join` by rewriting the query or adjusting `join_collapse_limit`. |
| Actual rows >> estimated rows | Stale statistics — run `ANALYZE <table>` to refresh. |

### Refreshing statistics

If estimates diverge significantly from actual row counts, the planner is
working with stale statistics. Refresh manually:

```sql
ANALYZE inventory.stock_entries;
-- or across all tables in a schema:
ANALYZE;
```

The autovacuum daemon refreshes statistics automatically, but it may lag on
tables with heavy insert/update workloads. Consider tuning
`autovacuum_analyze_scale_factor` on particularly active tables.

## Workflow summary

```
1. Tail postgres logs → identify slow query strings.
2. Query pg_stat_statements → rank by total_exec_time / mean_exec_time.
3. Copy the worst offender → run EXPLAIN (ANALYZE, BUFFERS).
4. Read the plan: look for Seq Scan, row-estimate divergence, buffer reads.
5. Add an index (or rewrite the query) → re-run EXPLAIN to confirm improvement.
6. Reset pg_stat_statements → let the app run → re-query to confirm total_exec_time dropped.
```

## Aspire local-dev

In the local development Aspire stack (not Docker Compose), postgres is a
container managed by the AppHost. The same SQL commands work — connect via the
connection string shown in the Aspire dashboard, or use the `psql` container exec
approach above substituting the Aspire-managed container name.

`pg_stat_statements` is **not** pre-loaded in the Aspire dev container (no
`shared_preload_libraries` override is configured there). To enable it locally
for a dev investigation, exec into the postgres container and run:

```sql
-- Requires the postgres container to be restarted with the flag,
-- or run as superuser inside the running container:
LOAD 'pg_stat_statements';  -- session-only; persists until container restart
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
```

For a persistent local setup, add the `-c shared_preload_libraries=pg_stat_statements`
flag to the postgres resource in `src/Plantry.AppHost/Program.cs` (see ADR-016 for the
Aspire resource configuration pattern).
