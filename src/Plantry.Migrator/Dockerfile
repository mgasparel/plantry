# syntax=docker/dockerfile:1
# Multi-stage Dockerfile for Plantry.Migrator (ADR-016 Decision 2)
# One-shot migration runner: applies EF migrations with owner credentials then exits.
# Build context: repo root (same as Plantry.Web Dockerfile)
# Build: docker build -f src/Plantry.Migrator/Dockerfile -t <image-ref> .

# ── Stage 1: restore ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

# Copy global.json and every .csproj (structure preserved via COPY --parents) so the
# restore layer is cached independently of source changes. The wildcard auto-discovers
# new projects — do NOT hand-list csprojs here (that list silently drifts when a bounded
# context is added, breaking the build; see plantry-vrht).
COPY global.json ./
COPY --parents src/**/*.csproj ./

RUN dotnet restore src/Plantry.Migrator/Plantry.Migrator.csproj

# ── Stage 2: build & publish ─────────────────────────────────────────────────
FROM restore AS publish
WORKDIR /src

COPY src/ src/

RUN dotnet publish src/Plantry.Migrator/Plantry.Migrator.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

# ── Stage 3: runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=publish /app/publish .

# Required env vars at runtime (injected by docker-compose.prod.yml):
#   ConnectionStrings__plantrydb  — full Postgres owner connection string
#   Database__AppUserPassword     — password to set on the least-privilege app_user role
ENTRYPOINT ["dotnet", "Plantry.Migrator.dll"]
