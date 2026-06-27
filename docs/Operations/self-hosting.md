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
| `DP_CERT_PASSWORD` | DataProtection key ring encryption passphrase — see below. |
| AI API key | **Optional.** Without it, the receipt/meal-plan AI shows a locked-feature UI and the rest of the app works normally. Operators bring their own key. |

Demo/seed data is Development-only, so a self-hosted instance starts empty.

## DataProtection key ring encryption

ASP.NET Core uses a **DataProtection key ring** to sign and encrypt auth
cookies, antiforgery tokens, and session data. Without encryption, the key
ring XML files are stored in plaintext inside the `dp_keys` Docker volume.

Plantry encrypts the key ring at rest using a self-signed X.509 certificate.

### How it works

1. On first `docker compose up -d`, the **`dp-cert-init`** one-shot service
   runs `openssl` to generate a 2048-bit RSA self-signed certificate (10-year
   validity) and exports it as a password-protected PKCS#12 file at
   `/certs/dp.pfx` inside the `dp_certs` named volume.

2. **`plantry-web`** starts only after `dp-cert-init` exits successfully. It
   reads `/certs/dp.pfx` using `DP_CERT_PASSWORD` and passes the certificate
   to `AddDataProtection().ProtectKeysWithCertificate(...)`.

3. On subsequent `docker compose up -d` runs the `dp-cert-init` service
   detects `/certs/dp.pfx` already exists and exits immediately without
   regenerating — the operation is idempotent.

### What you must do

Set `DP_CERT_PASSWORD` to a strong, random passphrase in your `.env` file
**before** the first `docker compose up`. The password protects the PFX file
on disk; the cert itself is self-signed and not trusted by any CA.

```bash
# Generate a suitable random password (example — use your preferred method):
openssl rand -base64 32
```

Add it to `.env`:

```
DP_CERT_PASSWORD=<your random password here>
```

### Stability guarantee

**Do not change `DP_CERT_PASSWORD` or remove the `dp_certs` volume** while the
app is running. The key ring is encrypted with the certificate currently in
`dp_certs`. If you remove the volume or change the password without
re-encrypting the keys, the app cannot decrypt existing keys and all active
sessions will be invalidated (users are logged out).

### Certificate rotation

For a household app the 10-year certificate validity means rotation is
effectively never needed. If you must rotate:

1. Stop `plantry-web`:
   ```bash
   docker compose stop plantry-web
   ```

2. Remove the `dp_keys` and `dp_certs` volumes (this invalidates all sessions):
   ```bash
   # Remove only the cert and key ring volumes — postgres_data is untouched.
   docker volume rm $(docker compose config --volumes | grep -E '^dp_(keys|certs)$' | xargs -I{} docker compose config --volumes)
   # Simpler alternative — identify by stack prefix (default: directory name):
   docker volume rm <stack>_dp_certs <stack>_dp_keys
   ```

3. Update `DP_CERT_PASSWORD` in `.env`.

4. Bring the stack back up — `dp-cert-init` will generate a fresh certificate:
   ```bash
   docker compose up -d
   ```

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
