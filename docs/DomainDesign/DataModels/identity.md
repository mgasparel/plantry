# Context 1 — Identity & Access (`identity` schema) ✅

**Membership and authentication are handled by ASP.NET Core Identity.** We do **not** hand-roll user, password, or session tables — Identity owns email, password hashing, security stamp, lockout, and the auth cookie/session machinery. We own the household, its settings, and invites.

- Extend `IdentityUser<Guid>` with `HouseholdId` (→ `household`) and `DisplayName`.
- **Membership = `HouseholdId` on the user.** v1 is flat (no roles), a user belongs to exactly one household, so there is **no join table**. (A `user_household` many-to-many is the deferred shape if a user ever joins multiple households.)
- The `AspNet*` Identity tables live in the `identity` schema alongside our own.

---

**`household`** — the tenant root (the one table with a bare single-column PK)

| Column | Type | Notes |
|---|---|---|
| `household_id` | `uuid` PK | tenant root |
| `name` | `text` | |
| `created_at` / `updated_at` | `timestamptz` | |

---

**`household_settings`** — 1:1 with household (SPEC §7d/§7f)

| Column | Type | Notes |
|---|---|---|
| `household_id` | `uuid` PK | FK → `household` (also the PK — 1:1) |
| `expiry_warning_days` | `int` | default 7 (SPEC §7f) |
| `theme` | `text` | `light` / `dark` / `system` |
| `email_intake_address` | `text` null | forwarding address (SPEC §7d) |
| `ai_api_key_encrypted` | `bytea` null | **per-household key, encrypted at rest** via ASP.NET Core Data Protection; decrypted server-side only, **never serialized to the client** (form shows "key set ••••"). Reconciles user-entered keys with ADR-007's "held server-side, never exposed to client." |
| `created_at` / `updated_at` | `timestamptz` | |

---

**`household_invite`** — the two-step "household issues invite → user accepts" (ADR-010); Identity has no invite concept

| Column | Type | Notes |
|---|---|---|
| `invite_id` | `uuid` PK | |
| `household_id` | `uuid` | FK → `household` |
| `email` | `citext` | invitee |
| `token` | `text` | unique; accept-link secret |
| `status` | `text` | `pending` / `accepted` / `revoked` / `expired` (CHECK) |
| `invited_by_user_id` | `uuid` | FK → Identity user |
| `created_at` / `expires_at` / `accepted_at` | `timestamptz` | |

---

> **ADR note (reconciled):** ADR-008 now records the delegation of auth/membership/sessions to ASP.NET Core Identity, and ADR-007 the per-household AI key encrypted at rest in `household_settings` — both amended 2026-06-06.
