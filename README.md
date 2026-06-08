# Plantry

> Smart household inventory, built for the way people actually cook and shop.

Plantry is a household inventory and kitchen intelligence app. At its core: a live picture of what you have at home, what it's worth, and what you can make with it.

It does three things better than anything else:

1. **Intake is nearly automatic.** Photograph a receipt — or forward it by email — and Plantry reads it, maps every item to your catalog, and queues it for a fast review.
2. **Recipes are connected to reality.** Every recipe shows what percentage of its ingredients you have on hand, what it'll cost to make, and whether anything is about to expire.
3. **The app thinks ahead.** An AI planner generates a week of meals around your inventory, your preferences, and what's on sale — prioritizing expiring ingredients and minimizing waste.

---

## Tech stack

| Layer | Choice |
|---|---|
| Backend / domain | .NET 10 (C#) |
| Persistence | PostgreSQL |
| UI rendering | Razor Pages/MVC (server-rendered hypermedia) |
| UI interactivity | htmx + Alpine.js — no Node, no bundler |
| AI orchestration | Server-side .NET (`ChatClient`, OpenAI-compatible) |
| Container / deployment | Docker + .NET Aspire app model |

Plantry is a **modular monolith**: one .NET process, one PostgreSQL database, organized into bounded contexts (Identity, Catalog, Inventory, Intake, Pricing, Shopping, and more).

---

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (used by .NET Aspire to run PostgreSQL and other resources locally)
- [Aspire CLI](https://aspire.dev/get-started/install-cli/)

### Run the app

The solution is orchestrated by [ Aspire](https://aspire.dev/) via `Plantry.AppHost`, which provisions PostgreSQL and runs the web app:

```sh
cd .\src\Plantry.AppHost\
aspire run
```

This opens the Aspire dashboard, from which you can reach the running web app and inspect logs, traces, and resources.

### Build

```sh
dotnet restore
dotnet build
```

---

## Testing

Tests are organized by layer:

| Project | What it covers |
|---|---|
| `Plantry.Tests.Unit` | Domain unit tests |
| `Plantry.Tests.Architecture` | Bounded-context boundary rules |
| `Plantry.Tests.Integration` | Real-Postgres integration tests (via Testcontainers) |
| `Plantry.Tests.E2E` | End-to-end tests against a running app |

Run the fast suites:

```sh
dotnet test tests/Plantry.Tests.Unit
dotnet test tests/Plantry.Tests.Architecture
```

Integration tests need a Postgres connection string:

```sh
  dotnet test tests/Plantry.Tests.Integration
```

E2E tests require a running instance of the app and are not part of the standard PR pipeline (see `.github/workflows/ci.yml`).

### Mutation testing

Domain logic in `Plantry.SharedKernel` and `Plantry.Catalog` (the conversion-resolution and
product-invariant core — `UnitConverter`, `Product`, `ExpiryDefaultResolver`) is checked with
[Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/). Stryker mutates one
source project per run, so each domain has its own config:

```sh
dotnet stryker --config-file stryker-config.json
dotnet stryker --config-file stryker-config.catalog.json
```

A surviving mutant is a test gap — the threshold breaks the build below 60% mutation score.

---

## Continuous integration

Every push to `main` and `slice/**`, and every PR into `main`, runs through GitHub Actions (`.github/workflows/ci.yml`): restore, build, unit/architecture/integration tests, and a code coverage gate. See the workflow file for exact thresholds.

---

## Repository layout

```
code/
├── src/
│   ├── Plantry.AppHost              # Aspire orchestration (run this to start the app)
│   ├── Plantry.ServiceDefaults      # Shared service configuration (telemetry, health checks, ...)
│   ├── Plantry.Web                  # Razor Pages/MVC web app — UI and composition root
│   ├── Plantry.SharedKernel         # Cross-context primitives (IDs, value objects, domain events)
│   └── Plantry.<Context>[.Infrastructure]
│                                    # One pair per bounded context — domain + EF Core persistence
│                                    # (Identity, Catalog, Inventory, Intake, Pricing, Shopping, ...)
└── tests/
    ├── Plantry.Tests.Unit
    ├── Plantry.Tests.Architecture
    ├── Plantry.Tests.Integration
    └── Plantry.Tests.E2E
```
