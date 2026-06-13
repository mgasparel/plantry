# Cross-cutting conventions ✅

These apply to every table unless a context notes an exception.

| Decision | Choice | Rationale / trade-off |
|---|---|---|
| **Primary keys** | `uuid` (UUIDv7) | Time-ordered for index locality; non-enumerable; generated app-side in .NET so the domain owns identity. 16 bytes vs `bigint`'s 8 — irrelevant at this scale. |
| **Tenant key pattern** | Single-column `uuid` PK + non-null `household_id` column on every root; **RLS** is the actual isolation mechanism (ADR-008). | Composite `(household_id, id)` PKs were considered and **rejected** — RLS already isolates tenants, the cross-tenant-FK gap it would close is narrow, and composite keys fight ASP.NET Core Identity's single-column keys. |
| **Tenant-safe child FKs** | On an aggregate *parent*, add `UNIQUE (household_id, id)` and have children carry `(household_id, parent_id)` as a **composite FK** to it. | Gives the structural "child can't reference another tenant's parent" guarantee exactly where it matters (within aggregates), without burdening standalone tables. |
| **Context separation** | One **PostgreSQL schema per bounded context** (`identity`, `catalog`, `inventory`, …). | Makes ADR-010's "no context reads another's tables" enforceable via `search_path`/grants; module seams visible in the DB. |
| **Cross-context references** | **ID only, no enforced FK** across context boundaries. Hard FKs only *within* a context/aggregate. | Keeps contexts independently evolvable (the DDD intent). Cost: referential integrity for cross-context IDs (e.g. an Inventory `product_id`) is the application layer's responsibility. |
| **Timestamps** | `created_at` / `updated_at` as `timestamptz` (UTC). | |
| **Attribution** | `user_id` stamped on journal/audit rows (ADR-010). | |
| **Deletes** | Soft-delete (`archived_at`) for Catalog reference data; journal / price-observation tables are **append-only** — never updated or deleted. | A product referenced by years of journal history can't be hard-deleted. Corrections are new rows, not edits. |
| **Money / quantity** | `numeric(12,2)` money, `numeric(12,3)` quantity; conversion factors `numeric(18,6)`. Single currency for v1 (presumed CAD). | Multi-currency deferred. |
| **Enums** | C# enum in the domain, persisted as `text` + `CHECK` constraint. | Readable in the DB, no fragile `ALTER TYPE`. Applies to `unit.dimension`, `location.type`, journal `reason`, etc. |
