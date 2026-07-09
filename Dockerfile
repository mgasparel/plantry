# Multi-stage Dockerfile for Plantry.Web (ADR-016 Decision 1)
# Build: docker build -t <image-ref> .
# Run:   docker run -p 8080:8080 \
#           -e ConnectionStrings__plantrydb="Host=...;Database=plantrydb;Username=postgres;Password=..." \
#           -e Database__AppUserPassword="..." \
#           <image-ref>

# ── Stage 1: restore ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

# Copy global.json and every .csproj that Plantry.Web (transitively) depends on,
# preserving directory structure, so the restore layer is cached independently of
# source changes. We restore the Web project directly (not solution-level) so test
# projects are not needed here and do not bust the restore-layer cache on test edits.
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
COPY src/Plantry.Deals/Plantry.Deals.csproj                                             src/Plantry.Deals/
COPY src/Plantry.Deals.Infrastructure/Plantry.Deals.Infrastructure.csproj               src/Plantry.Deals.Infrastructure/
COPY src/Plantry.Ai.Infrastructure/Plantry.Ai.Infrastructure.csproj                     src/Plantry.Ai.Infrastructure/
COPY src/Plantry.Composition/Plantry.Composition.csproj                                 src/Plantry.Composition/
COPY src/Plantry.Migration.Grocy/Plantry.Migration.Grocy.csproj                        src/Plantry.Migration.Grocy/
COPY src/Plantry.ServiceDefaults/Plantry.ServiceDefaults.csproj                         src/Plantry.ServiceDefaults/
COPY src/Plantry.Web/Plantry.Web.csproj                                                 src/Plantry.Web/
COPY src/Plantry.Migrator/Plantry.Migrator.csproj                                       src/Plantry.Migrator/
COPY src/Plantry.AppHost/Plantry.AppHost.csproj                                         src/Plantry.AppHost/

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
