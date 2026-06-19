# Deployment runbook — production (single host + Docker Compose)

> **How** Plantry is deployed and operated in production. The **why** lives in
> [ADR-016](../ADRs/ADR-016.md) (pipeline) and [ADR-017](../ADRs/ADR-017.md) (migrations);
> the runtime shape in [ADR-012](../ADRs/ADR-012.md). For third-party self-hosting see
> [self-hosting.md](self-hosting.md). For the not-yet-built rollout, see
> [cicd-rollout-plan.md](cicd-rollout-plan.md).

Production is one Linux host running `docker compose`. There is no Kubernetes, no
managed control plane, and no Aspire AppHost at runtime — the AppHost is a
local-development tool only.

## Topology

```
┌─ docker compose on the host ───────────────────────────┐
│  caddy        reverse proxy, automatic TLS      :443    │
│     └─▶ plantry-web   (image from GHCR, app_user creds) │
│            └─▶ postgres   (data in a named volume)      │
│  migrator     one-shot; runs on deploy, then exits      │
└─────────────────────────────────────────────────────────┘
```

- **plantry-web** — the app. Connects to Postgres as the least-privilege `app_user`
  role (never the owner) so RLS applies. Holds the AI API key (optional).
- **migrator** — applies EF migrations with the owner connection and reconciles the
  `app_user` password from config, then exits (ADR-017). Gates web via
  `depends_on: service_completed_successfully`.
- **postgres** — system of record; data on a named volume.
- **caddy** — TLS termination + reverse proxy. Alternatives: Traefik, nginx.

## Prerequisites (one-time)

- A Linux host with Docker Engine + the Compose plugin.
- DNS A/AAAA record pointing at the host (for Caddy's automatic TLS).
- An SSH key the CI deploy job can use (stored as a GitHub Actions secret).
- The host logged in to GHCR if images are private (`docker login ghcr.io`); public
  images need no login.

## Secrets / configuration

Runtime secrets live in a root-owned `.env` file (mode `600`) next to the Compose
file, referenced by `docker-compose.prod.yml`. Three matter:

| Variable | Used by | Notes |
|---|---|---|
| `POSTGRES_PASSWORD` | postgres, migrator | Database **owner**. Postgres only applies it on **first** volume init — pin it once and keep it stable. |
| `APP_USER_PASSWORD` | migrator, plantry-web | The least-privilege runtime role. The migrator `ALTER ROLE`s `app_user` to this on every run (ADR-017); the web app connects with it. Rotatable: change here, re-run the migrator. |
| AI API key | plantry-web | Optional. Absent → the receipt/plan AI degrades to a locked-feature UI, the app still runs. |

CI→deploy secrets (SSH key, registry credentials) live in GitHub Actions
repository secrets / environments — never in the repo.

## Deploy procedure

What CD does on a merge to `main` (and the exact manual equivalent):

```bash
# 1. (CI) build + push image, tagged with the commit SHA
docker build -t ghcr.io/<org>/plantry-web:<sha> .
docker push ghcr.io/<org>/plantry-web:<sha>

# 2. on the host, from the compose directory:
export PLANTRY_WEB_IMAGE=ghcr.io/<org>/plantry-web:<sha>
docker compose -f docker-compose.prod.yml pull

# 3. migrations — explicit, fails loud, blocks the deploy (ADR-017)
docker compose -f docker-compose.prod.yml run --rm migrator

# 4. roll the web service to the new image
docker compose -f docker-compose.prod.yml up -d plantry-web

# 5. smoke check
curl -fsS https://<host>/alive
```

If step 3 exits non-zero, **stop** — the schema is unchanged and the old web
container is still serving. Investigate before proceeding.

## Health checks

`MapDefaultEndpoints` only maps `/health` and `/alive` in **Development**
(`ServiceDefaults/Extensions.cs`). For production probing, map `/alive`
unconditionally (it is just the liveness self-check) and keep the fuller
`/health` gated or bound to an internal interface. The Compose healthcheck and
the post-deploy smoke step both target `/alive`. **This change is a prerequisite —
see the rollout plan.**

## Rollback

Forward-fix is preferred, but to revert the app: re-point `PLANTRY_WEB_IMAGE` at
the previous SHA tag and `docker compose up -d plantry-web`. **Schema does not roll
back** — EF down-migrations are not run in production. If a migration must be
undone, write a new forward migration that reverses it. This is why backups
(below) gate any risky migration.

## Backups (non-negotiable once real data lands)

You own the host, so you own backups. At minimum: a `pg_dump` cron writing to
**off-host** storage, plus a periodic volume snapshot. Test a restore before you
need one. Always back up immediately before a deploy that includes a destructive
migration.

## Notes / divergences

- Production uses a hand-maintained `docker-compose.prod.yml`, **not** the
  Aspire-generated `aspire-output/docker-compose.yaml` — the generated file bundles
  the Aspire dashboard and passes the Postgres superuser password to the web app.
  See the ADR-012 amendment and [ADR-016](../ADRs/ADR-016.md).
