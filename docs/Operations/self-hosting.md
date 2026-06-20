# Self-hosting Plantry

> How a third party runs their own Plantry instance. The decision context is
> [ADR-016](../ADRs/ADR-016.md) (release/distribution) and
> [ADR-017](../ADRs/ADR-017.md) (migrations). Your own production deployment is
> [deployment.md](deployment.md).
>
> **Status: planned.** We build as if Plantry will be open-sourced; the decision to
> actually publish is deferred but does **not** block this work. The pipeline and
> artifacts are built OSS-ready — what waits on the decision is only flipping
> repository/image visibility and standing up the contributor surface.

A self-hosted instance is the same container stack as production, run by the
operator on their own hardware. Your CI/CD does **not** reach into a self-hosted
instance — the operator updates it by pulling new images.

## What you ship

| Artifact | Notes |
|---|---|
| **Public images** | `plantry-web` and `plantry-migrator` on GHCR, marked public (no pull token). |
| **Version tags** | Semver is the public contract: `:1.4.0`, `:1.4`, `:1`, plus `:latest`. Operators pin `:1` or `:1.4`. |
| **`docker-compose.yml` + `.env.example`** | The actual product for self-hosters — published as a release asset. Differs from your prod compose mainly in not assuming your specific host/proxy. |
| **Optional `docker-compose.caddy.yml`** | TLS overlay, opt-in. Default compose exposes a plain port so operators can front it with their existing proxy. |

## How updates and migrations work

The operator's entire update flow:

```bash
# back up first — see below
docker compose pull
docker compose up -d
```

On `up`, the **migrator** one-shot runs before the web app starts, gated by
`depends_on: service_completed_successfully` (ADR-017). It applies all pending
migrations in order and reconciles the `app_user` password from `.env`, then exits;
the web app starts only if it succeeds. `MigrateAsync` is idempotent, so re-running
is a no-op when already current, and a multi-version jump (e.g. `1.0 → 1.7`) applies
every intermediate migration correctly in one shot.

## Configuration

Required in `.env`:

| Variable | Notes |
|---|---|
| `POSTGRES_PASSWORD` | Database owner. Only applied on first volume init — set once. |
| `APP_USER_PASSWORD` | Least-privilege runtime role; the migrator sets it, the app uses it. |
| AI API key | **Optional.** Without it, the receipt/meal-plan AI shows a locked-feature UI and the rest of the app works normally. Operators bring their own key. |

Demo/seed data is Development-only, so a self-hosted instance starts empty.

## Upgrade contract

- **Back up before every upgrade.** `pg_dump` to off-host storage.
- **Pin a major (or minor) tag**, e.g. `:1`. `:latest` can deliver a breaking
  change on the next `up`. A breaking change is signalled by a major version bump.
- **No safe downgrade.** Rolling an image tag back does **not** roll back schema
  the migrator already applied. Recovery from a bad upgrade is: restore the backup,
  then pin the previous version. The project's policy is forward-fix, not rollback.
- **Don't skip the backup on releases whose notes flag a destructive migration.**

## When Plantry goes public

The OSS publish surface is now in place:

- **Changelog and support matrix** — [`CHANGELOG.md`](../../CHANGELOG.md) at the repo root defines the semver tag convention and the version support policy.
- **Contributor docs** — [`CONTRIBUTING.md`](../../CONTRIBUTING.md) explains the current policy (outside contributions not accepted), the agentic workflow, and how to report issues.
- **PR template** — [`.github/PULL_REQUEST_TEMPLATE.md`](../../.github/PULL_REQUEST_TEMPLATE.md) guides both agent and human PR authors.
- **CODEOWNERS** — [`.github/CODEOWNERS`](../../.github/CODEOWNERS) requires explicit maintainer approval before any PR can merge.
- **Public-PR runner policy** — [`docs/Operations/public-pr-runner-policy.md`](public-pr-runner-policy.md) documents how fork PRs are handled safely (no secrets in `pull_request` jobs, no self-hosted runners for fork-triggered jobs).

The only remaining step at publish time is flipping repository and container-image visibility to public.
