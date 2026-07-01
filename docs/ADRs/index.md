# ADR Index

Architecture Decision Records for Plantry.

| ADR | Title | Status |
|-----|-------|--------|
| [ADR-001](ADR-001.md) | DDD as architectural foundation | Accepted |
| [ADR-002](ADR-002.md) | .NET backend | Accepted |
| [ADR-003](ADR-003.md) | PostgreSQL | Accepted |
| [ADR-004](ADR-004.md) | Server-rendered hypermedia, not a SPA | Accepted |
| [ADR-005](ADR-005.md) | htmx + Alpine, no front-end framework / no Node toolchain | Accepted · amended 2026-06-22 (escape hatch triggered; now the default, see [ADR-020](ADR-020.md)) |
| [ADR-006](ADR-006.md) | No GraphQL; REST + read models (and any MCP) over Plantry's own application services | Accepted |
| [ADR-007](ADR-007.md) | AI orchestration runs server-side in .NET | Accepted · amended 2026-06-06 |
| [ADR-008](ADR-008.md) | Authentication and household multi-tenancy built in from day one | Accepted · amended 2026-06-06 |
| [ADR-009](ADR-009.md) | Binary content stored in PostgreSQL | Accepted |
| [ADR-010](ADR-010.md) | Bounded contexts and aggregate boundaries (modular monolith) | Accepted · amended 2026-06-06 |
| [ADR-011](ADR-011.md) | Single consumption primitive (Inventory `Consume`) | Accepted · amended 2026-06-06 |
| [ADR-012](ADR-012.md) | Deployment & runtime topology (Docker homelab, Aspire app model) | Superseded by [ADR-016](ADR-016.md) |
| [ADR-013](ADR-013.md) | Intake review form: targeted out-of-band swaps over full-region re-render | Accepted · amended 2026-06-18, 2026-06-22 · superseded-in-practice on island surfaces by [ADR-020](ADR-020.md) |
| [ADR-014](ADR-014.md) | Cross-context writes are eventually consistent, never one shared transaction | Accepted |
| [ADR-015](ADR-015.md) | Operational/maintenance endpoints may return JSON; UI handlers stay hypermedia | Accepted · amended 2026-06-22 (third category: island data endpoints) |
| [ADR-016](ADR-016.md) | CI/CD pipeline and release process (build → GHCR → Compose; CI as a serial merge gate) — supersedes ADR-012 | Accepted |
| [ADR-017](ADR-017.md) | Database migrations applied by an explicit one-shot migrator, not on app startup | Accepted |
| [ADR-018](ADR-018.md) | Fully agentic engineering | Accepted |
| [ADR-019](ADR-019.md) | Skills until they break: keep agentic tooling minimal, escalate on concrete triggers | Accepted |
| [ADR-020](ADR-020.md) | Reactive islands for stateful surfaces (Intake, Meal Planner, Take Stock) — amends ADR-005/013/015 | Accepted · amended 2026-06-24 (test-time Node permitted for island tests) |
| [ADR-021](ADR-021.md) | Read paths may cross bounded-context boundaries via read-only cross-schema read models (read-side dual of ADR-014) | Accepted |
