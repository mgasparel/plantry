# syntax=docker/dockerfile:1
# Multi-stage Dockerfile for Plantry.Web (ADR-016 Decision 1)
# Build: docker build -t <image-ref> .
# Run:   docker run -p 8080:8080 \
#           -e ConnectionStrings__plantrydb="Host=...;Database=plantrydb;Username=postgres;Password=..." \
#           -e Database__AppUserPassword="..." \
#           <image-ref>

# ── Stage 1: restore ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

# Copy global.json and every .csproj (structure preserved via COPY --parents) so the
# restore layer is cached independently of source changes. The wildcard auto-discovers
# new projects — do NOT hand-list csprojs here (that list silently drifts when a bounded
# context is added, breaking the build; see plantry-vrht). We restore the Web project
# directly (not solution-level), so test projects are not needed here and test edits do
# not bust the restore-layer cache.
COPY global.json ./
COPY --parents src/**/*.csproj ./

RUN dotnet restore src/Plantry.Web/Plantry.Web.csproj

# ── Stage 2: build & publish ─────────────────────────────────────────────────
FROM restore AS publish
WORKDIR /src

# Copy full source (restore cache already warm)
COPY src/ src/

RUN dotnet publish src/Plantry.Web/Plantry.Web.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

# ── Stage 3: runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Kestrel listens on 8080 inside the container (standard non-root port for containers).
# The host-level port mapping is the operator's responsibility (-p 80:8080 or via Compose).
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

COPY --from=publish /app/publish .

# Connection string and app_user password are injected at runtime — never baked into the image.
# Required at startup:
#   ConnectionStrings__plantrydb  — full Postgres owner connection string
#   Database__AppUserPassword     — password for the least-privilege app_user role
ENTRYPOINT ["dotnet", "Plantry.Web.dll"]
