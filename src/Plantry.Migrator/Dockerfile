# Multi-stage Dockerfile for Plantry.Migrator (ADR-016 Decision 2)
# One-shot migration runner: applies EF migrations with owner credentials then exits.
# Build context: repo root (same as Plantry.Web Dockerfile)
# Build: docker build -f src/Plantry.Migrator/Dockerfile -t <image-ref> .

# ── Stage 1: restore ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

# Copy global.json and every .csproj that Plantry.Migrator (transitively) depends on.
# Structured to keep the restore layer cached independently of source changes.
COPY global.json ./

COPY src/Plantry.SharedKernel/Plantry.SharedKernel.csproj                              src/Plantry.SharedKernel/
COPY src/Plantry.Identity/Plantry.Identity.csproj                                       src/Plantry.Identity/
COPY src/Plantry.Identity.Infrastructure/Plantry.Identity.Infrastructure.csproj         src/Plantry.Identity.Infrastructure/
COPY src/Plantry.Catalog/Plantry.Catalog.csproj                                         src/Plantry.Catalog/
COPY src/Plantry.Catalog.Infrastructure/Plantry.Catalog.Infrastructure.csproj           src/Plantry.Catalog.Infrastructure/
COPY src/Plantry.Inventory/Plantry.Inventory.csproj                                     src/Plantry.Inventory/
COPY src/Plantry.Inventory.Infrastructure/Plantry.Inventory.Infrastructure.csproj       src/Plantry.Inventory.Infrastructure/
COPY src/Plantry.Pricing/Plantry.Pricing.csproj                                         src/Plantry.Pricing/
COPY src/Plantry.Pricing.Infrastructure/Plantry.Pricing.Infrastructure.csproj           src/Plantry.Pricing.Infrastructure/
COPY src/Plantry.Intake/Plantry.Intake.csproj                                           src/Plantry.Intake/
COPY src/Plantry.Intake.Infrastructure/Plantry.Intake.Infrastructure.csproj             src/Plantry.Intake.Infrastructure/
COPY src/Plantry.Recipes/Plantry.Recipes.csproj                                         src/Plantry.Recipes/
COPY src/Plantry.Recipes.Infrastructure/Plantry.Recipes.Infrastructure.csproj           src/Plantry.Recipes.Infrastructure/
COPY src/Plantry.Shopping/Plantry.Shopping.csproj                                       src/Plantry.Shopping/
COPY src/Plantry.Shopping.Infrastructure/Plantry.Shopping.Infrastructure.csproj         src/Plantry.Shopping.Infrastructure/
COPY src/Plantry.MealPlanning/Plantry.MealPlanning.csproj                               src/Plantry.MealPlanning/
COPY src/Plantry.MealPlanning.Infrastructure/Plantry.MealPlanning.Infrastructure.csproj src/Plantry.MealPlanning.Infrastructure/
COPY src/Plantry.ServiceDefaults/Plantry.ServiceDefaults.csproj                         src/Plantry.ServiceDefaults/
COPY src/Plantry.Migrator/Plantry.Migrator.csproj                                       src/Plantry.Migrator/

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
