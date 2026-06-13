# Identity & Access — Domain Model

> **Status:** Implemented — Phase 1. Retrospective backfill; the DDD process was formalized after these contexts were designed.
>
> **Purpose:** The tenant and authentication boundary. Owns the `Household` root and delegates auth entirely to ASP.NET Core Identity. Every other context's data belongs to a Household — `household_id` and `user_id` originate here.
>
> **Bounded context:** Identity (`identity` schema, Phase 1). The most upstream context: provides identity data, reads nothing from others.
>
> **Code shape:** `IdentityUser<Guid>` extended with `HouseholdId` + `DisplayName`. `Household`, `HouseholdSettings`, and `HouseholdInvite` are plain C# aggregates/entities persisted via EF Core.

---

## DDD Process

```
User Journeys  →  Ubiquitous Language  →  Domain Model (← here)  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregate map

| Aggregate root | Identity | Owns (composition) | Lifecycle |
|---|---|---|---|
| **Household** | `HouseholdId` (`uuid`) | `HouseholdSettings` (1:1), `HouseholdInvite[]` | Created on first-user registration; never deleted in v1 |
| **IdentityUser** (delegated) | `Guid` (ASP.NET Core Identity) | — | Managed by ASP.NET Core Identity; extended with `HouseholdId` + `DisplayName` |

`HouseholdSettings` is a 1:1 child (PK = `household_id`); it is created with the household and always exists. `HouseholdInvite` has its own lifecycle (pending → accepted / revoked / expired) but no existence outside its parent Household.

---

## Invariants

| # | Invariant | Enforced |
|---|---|---|
| **R1** | A user belongs to exactly one Household in v1 (`HouseholdId` on `IdentityUser`) | EF / app layer |
| **R2** | `HouseholdSettings` is created atomically with the Household; it is always present | App service |
| **R3** | `ai_api_key_encrypted` is stored via ASP.NET Core Data Protection and **never serialized to the client** — the UI shows only a masked indicator | App service |
| **R4** | `HouseholdInvite.token` is globally unique; only `pending` tokens are valid; accept is a one-way transition | App + DB unique index |
| **R5** | Invite `expires_at` is checked on accept; expired invites cannot be accepted | App service |

---

## Cross-context ports

Identity is the most upstream context. It **provides** identity data; it **reads nothing** cross-context.

| What Identity provides | Consumed by |
|---|---|
| `household_id` (the tenancy key, present on every table) | All contexts — Row-Level Security anchors on it |
| `user_id` (attribution soft-ref) | Inventory (`user_id` on journal entries), Intake (`created_by_user_id`), Shopping (`checked_by`), Recipes (`cooked_by` on `CookEvent`), Pricing (`user_id`) |
| `email_intake_address` from `HouseholdSettings` | Intake (async email receipt path) |
| `expiry_warning_days` from `HouseholdSettings` | Inventory / Pantry view (configurable expiry badge threshold) |

---

## Key decisions

- **DM-6:** Auth, membership, and sessions delegated entirely to ASP.NET Core Identity — no hand-rolled user/password/session tables. `IdentityUser<Guid>` is extended, not replaced.
- **DM-7:** Per-household AI API key is stored encrypted at rest in `HouseholdSettings.ai_api_key_encrypted` (ASP.NET Core Data Protection). Decrypted server-side only; never in a client response.
- **Flat membership (v1):** `HouseholdId` is a column on the user row — no join table. A `user_household` many-to-many is the non-breaking deferred shape for multi-household membership.

> Full schema: [../DataModels/identity.md](../../DataModels/identity.md)
