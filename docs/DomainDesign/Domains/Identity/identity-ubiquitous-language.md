# Identity & Access — Ubiquitous Language

> **Status:** Vocabulary confirmed — Phase 1 (backfilled)
>
> **Purpose:** The shared vocabulary for the Identity & Access context. Terms here — especially `Household` and `household_id` — appear verbatim across every other bounded context.
>
> **Bounded context:** Identity (`identity` schema, Phase 1).

---

## DDD Process

```
User Journeys  →  Ubiquitous Language (← here)  →  Domain Model  →  Data Schema  →  App Services  →  UI Slices
```

---

## Aggregates & Entities

| Term | Kind | Definition |
|---|---|---|
| **Household** | Aggregate root | The tenant unit. All data in Plantry belongs to a Household. Created when the first member registers; never deleted. |
| **HouseholdSettings** | Value / 1:1 child | Per-household configuration: theme, email intake address, encrypted AI key. Always exists (created with the Household). |
| **HouseholdInvite** | Entity (child of Household) | A time-limited, token-secured invitation issued to an email address. Status: `pending` → `accepted` / `revoked` / `expired`. |
| **User** (delegated) | ASP.NET Core `IdentityUser<Guid>` | Extended with `HouseholdId` (membership) and `DisplayName`. Auth, sessions, and passwords are fully managed by ASP.NET Core Identity. |

---

## Key Terms

| Term | Definition |
|---|---|
| **`household_id`** | The tenancy key. Present as a column on every table in every context. Row-Level Security (RLS) policies scope queries to the current household. Not a cross-context FK — a soft-ref by convention (DM-3). |
| **`user_id`** | Attribution key. A `Guid` soft-ref to an ASP.NET Core Identity user — used by Inventory (journal), Intake, Shopping, and Recipes for attribution, never as an enforced FK. |
| **Member** | A `User` with a `HouseholdId`. v1: flat — no roles, no tiers, one household per user. |
| **Invite** | A `HouseholdInvite` in `pending` status. The accept link carries the unique `token`. |
| **AI key** | The `ai_api_key_encrypted` field on `HouseholdSettings` — the per-household OpenAI-compatible API key, stored via ASP.NET Core Data Protection. Never sent to the client. |
| **DisplayName** | The user-facing name shown in attribution contexts (cook history, check-off, etc.). Stored on the extended `IdentityUser`. |
| **Theme** | `light` / `dark` / `system` — per-household UI preference on `HouseholdSettings`. |
| **Email intake address** | The forwarding address in `HouseholdSettings` used by the async email receipt path (Intake context). |

---

## Key Actions

| Verb | Meaning |
|---|---|
| **Register** | Create a Household and the first User in one flow. Triggers household seeding in all contexts (units, categories, locations, recipe tags). |
| **Invite** | A member issues a `HouseholdInvite` to an email address; generates the unique-token accept link. |
| **Accept** | Invitee follows the accept link, creates a User attached to the Household; invite transitions to `accepted`. |
| **Sign in / Sign out** | Delegated entirely to ASP.NET Core Identity (cookie auth). Not modelled as Household domain events. |
