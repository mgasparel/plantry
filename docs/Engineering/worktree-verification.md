# Worktree verification: booting an isolated app instance

A worker agent in a git worktree often needs to launch the full Plantry stack to verify a change visually
(drive `/Deals/Review`, screenshot a page). The problem: the developer's primary stack is usually already
running, and its fixed ports and Docker container/volume identities collide with a second boot.

**Aspire 13 solves most of this natively.** `aspire start --isolated` gives each run its own instance id,
**randomized ports** for the dashboard and every service endpoint (the `launchSettings` pins are overridden
and service discovery adjusts automatically), **isolated user-secrets**, and supports multiple simultaneous
instances by design. No launch-profile or port work is needed.

**Two gaps Aspire's isolated mode does not close, both gated behind an explicit `PLANTRY_VERIFY` switch in
`src/Plantry.AppHost/Program.cs` (plantry-q9zr.14):**

1. **The named Postgres data volume.** The AppHost pins `AddPostgres(...).WithDataVolume("plantrydb-data")`.
   Two Postgres containers mounting the same named volume on one data directory risks corruption — so a
   verify run runs **ephemeral Postgres** (no named volume — an empty DB) and skips **pgAdmin** (which pins
   its own container + host port).
2. **The AppHost's OWN dashboard / OTLP / resource-service endpoints.** `--isolated` randomizes *service*
   endpoints, but `Properties/launchSettings.json` pins the AppHost's dashboard (17250), OTLP (21137) and
   resource-service (22192) endpoints via env vars, and `aspire start` honours that profile — so a second
   instance collides with a running primary AppHost on 22192. (This was proven the hard way: the first live
   isolated trial died binding 22192.) A verify run therefore rebinds those three to port 0 (OS-assigned)
   *before* the builder reads configuration.

Aspire 13 exposes no public isolated-mode flag to auto-detect at build time, so the env var is the reliable
gate for both.

An empty DB means an empty review queue, so the `FakeDataSeeder` replays a checked-in real-ingest flyer
fixture (`src/Plantry.Web/Dev/Fixtures/superstore-flyer-2026-07.json`) into a current-week and a prior-week
flyer, giving `/Deals/Review` a deterministic three-tier queue that never drifts with the live flyer's
expiry. Seeding is a dev command (`POST /Dev/Seed`), not automatic — drive it after boot.

## The recipe (run from the worktree root)

This recipe was run live (2026-07-07) side-by-side with the primary stack and works end to end.

```bash
# 1. Enable the verify gate (ephemeral Postgres, no pgAdmin, AppHost infra ports rebound to 0).
export PLANTRY_VERIFY=true            # PowerShell: $env:PLANTRY_VERIFY = "true"

# 2. Boot a second full stack — randomized ports, isolated secrets, background (detached) start.
#    The AppHost declares a required `postgres-password` parameter (pinned in the developer's
#    user-secrets for the persistent volume). Isolated mode uses a SEPARATE secret store that does
#    not carry it, and the verify run's Postgres is ephemeral anyway — so supply any value inline.
#    (The env-var key uses `__` as the config `:` delimiter; the hyphen in the name is literal, so
#    on bash use `env` to set it — an inline `VAR=val` prefix rejects the hyphen.)
env "Parameters__postgres-password=verify-ephemeral-pw" \
  aspire start --isolated --non-interactive --nologo --format Json --apphost src/Plantry.AppHost
#    PowerShell: $env:Parameters__postgres-password = "verify-ephemeral-pw"; aspire start ...

# 3. Wait for the web app to become healthy.
aspire wait plantry-web --timeout 300

# 4. Discover this instance's randomized Web URL. `aspire describe` resolves to the AppHost in the
#    current directory, so it returns YOUR instance's resources even with the primary also running;
#    read plantry-web's `urls[].url`. `aspire ps --format Json` lists both AppHosts (yours + primary).
aspire describe --format Json

# 5. Seed the demo household + the flyer fixture into the fresh ephemeral DB (~8s).
#    (Replace <web-url> with the https endpoint from step 4; -k accepts the dev cert.)
curl -k -X POST <web-url>/Dev/Seed

# 6. Drive <web-url>/Deals/Review with the epic's Playwright recipe. Sign in as
#    demo@plantry.dev / demo1234. Expect a three-tier pending queue (401 none / 29 low /
#    21 high = 451 pending) for the CURRENT week; the prior week's expired-pending deals
#    must NOT appear (DD14).

# 7. Tear down YOUR instance only (never touches the primary stack). Target your worktree's
#    AppHost path so `aspire stop` disambiguates between the two running AppHosts.
aspire stop --apphost src/Plantry.AppHost
```

`aspire logs <resource>` gives failure triage if a resource never becomes healthy.

## Safety: never disturb the primary stack

- The gate only removes the volume mount + pgAdmin **when `PLANTRY_VERIFY` is set**; a normal
  `aspire start` (or production) is unchanged — it still mounts `plantrydb-data` and, in Development, runs
  pgAdmin.
- Prove the primary stack is untouched: it still answers on its fixed port (`curl -k https://localhost:7083`
  returns 200) before and after your run, and `docker volume inspect plantrydb-data` shows only the
  primary's Postgres container attached.

## The seeded fixture at a glance

- **Store**: Real Canadian Superstore (Flipp external id `8006782`), one standing subscription.
- **Current week** (`today-2 … today+5`, active): all 451 real deals, all **Pending**, in the original tier
  mix (401 none / 29 low / 21 high). The export's confirmed/rejected rows are seeded Pending here too, with
  their confidence preserved.
- **Prior week** (`today-9 … today-2`, expired; external id `8006782-prev`): a clone whose status pass runs
  through the **real** `ConfirmDeal` / `RejectDeal` verbs — the majority of the resolvable (suggestion-bearing)
  deals Confirmed (so `DealConfirmedEvent` fires and price observations land), six Rejected, and the rest left
  **Pending**. Those expired-Pending deals are the permanent DD14 tripwire: they must never appear in the
  review queue.

The seeder never invokes the AI `DealMatcher` — the fixture is already-matched results being replayed, which
keeps every visual check deterministic and API-key-free.
