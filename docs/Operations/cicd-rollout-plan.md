# CI/CD rollout plan

> The phased path from today's state to the target in [ADR-016](../ADRs/ADR-016.md)
> and [ADR-017](../ADRs/ADR-017.md). Operational detail lives in
> [deployment.md](deployment.md) and [self-hosting.md](self-hosting.md). No code has
> been written yet — this is the build order.

## Where we are today

- CI builds + tests on `main` / PRs but **does not gate** — the dev workflow merges
  to `main` locally and pushes, so CI runs after the fact (ADR-016 context).
- The app **migrates on startup** with owner credentials (ADR-017 context).
- There is **no container build, no deploy, and no CD**.

## Must-fix bugs (file these as beads; they block the phases below)

| # | Bug | Location | Fix |
|---|---|---|---|
| 1 | CI sets up .NET SDK `9.0.x` but all projects target `net10.0` | `.github/workflows/ci.yml` (`setup-dotnet`) | Add a `global.json` pinning SDK 10; bump `setup-dotnet` to `10.0.x`. Builds only "work" today by luck of the runner image. |
| 2 | `dotnet workload install aspire` is obsolete | `.github/workflows/ci.yml` | Aspire 9+/13 ships via NuGet (`Aspire.AppHost.Sdk`); remove the step — it is slow and can fail. |
| 3 | `app_user` password hard-coded in migration vs. required `Database:AppUserPassword` | `Plantry.Identity.Infrastructure/Migrations/…InitialIdentitySchema.cs` (`CREATE ROLE app_user … PASSWORD 'app_user_password'`) vs. `Plantry.Web/Program.cs` | Migrator reconciles the password from config (ADR-017). Without this, any operator-set password breaks DB auth. |
| 4 | Health endpoints (`/health`, `/alive`) are mapped in **Development only** | `Plantry.ServiceDefaults/Extensions.cs` (`MapDefaultEndpoints`) | Map `/alive` unconditionally for prod probing; keep `/health` gated/internal. Compose healthcheck + smoke step depend on it. |

## Phases

### Phase 1 — Make CI green and authoritative
*Prerequisite for everything; you cannot gate on a red or weaker-than-local CI.*

- Fix bugs **1** and **2**.
- Bring **E2E into CI** with Docker (decided: the full suite runs both locally as a
  pre-filter and in CI as the authoritative gate) so CI is never weaker than the local
  pre-flight. Add NuGet caching; consider splitting a fast check (build + unit + arch)
  from the E2E check.

### Phase 2 — Containerization
- Hand-written multi-stage `Dockerfile` for `Plantry.Web`.
- New `Plantry.Migrator` console project (ADR-017); remove/gate the startup-migration
  block in `Program.cs`; fix bug **3** in the migrator.
- `docker-compose.prod.yml` (postgres + web with `app_user` creds + migrator + caddy).
- Fix bug **4** (map `/alive` in prod).

### Phase 3 — CD deploy job
- GitHub Actions job: build/push image to GHCR → `ssh` → `pull` → `run --rm migrator`
  → `up -d` → smoke `/alive`.
- Stand up **backups** (`pg_dump` off-host) — non-negotiable once real data exists.

### Phase 4 — CI-gated serial workflow
- Branch protection on `main`: require the CI check **and route merges through a merge
  queue**. Enable repo "Allow auto-merge." Everything — agents and manual changes —
  merges via PR; nothing is pushed directly to `main` (the queue guards trunk if a
  manual push coincides with an open agent PR).
- Flip `implement-ticket-worker`: after commit, `git push` the `issue/<id>` branch and
  `gh pr create`; add `PR:` to the verdict.
- Update the merge step to `gh pr merge --auto`; move `bd close` to post-merge.
- Retire `pipeline-orchestrator` (it describes an unused parallel system) or reduce it
  to a minimal serial driver.

### Phase 5 — Self-host / OSS packaging *(built OSS-ready; the publish flip is deferred)*
- Tag-driven release workflow → semver public images + Compose/`.env.example` release
  assets ([self-hosting.md](self-hosting.md)).
- If OSS: `CONTRIBUTING`, PR template, `CODEOWNERS`, changelog/support-matrix
  discipline, hosted-runner policy for public PRs.

## Open decisions

None blocking. The project proceeds as if OSS-bound; the actual publish flip is
deferred but non-blocking (Phase 5, runner policy).

*Resolved 2026-06-19:* full suite incl. E2E runs both locally and in CI; merges go
through a merge queue; local pre-flight stays full (not thinned); proceed as if OSS-bound.

*Amended 2026-06-20:* the merge queue is **deferred until the repo is org-owned** (merge
queues are unavailable on personal-account repos); strict branch protection + the
orchestrator mergeability guard cover the concurrent-manual-push case meanwhile. See the
ADR-016 amendment under Decision 3.
