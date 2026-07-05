using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Ai.Infrastructure;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;
using Plantry.Composition;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Intake.Infrastructure;
using Plantry.Migration.Grocy;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
using Plantry.Web.MealPlanning;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;
using Plantry.Shopping.Infrastructure;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Background;
using Plantry.Web.Deals;
using Plantry.Web.Dev;
using Plantry.Web.Events;
using Plantry.Web.Intake;
using Plantry.Web.Inventory;
using Plantry.Web.Pricing;
using Plantry.Web.Recipes;
using Plantry.Web.Shopping;
using Plantry.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// DB readiness check: a single lightweight CanConnectAsync probe on the Identity context
// (representative of the shared database). Tagged "ready" so /ready reports Healthy/Unhealthy
// without leaking check names or exception detail. Not per-context: all contexts share one
// database, so one probe gives the full DB-connectivity signal at 1x probe cost (not 8x).
// Container healthcheck stays on /alive (liveness) — a DB blip must not trigger restarts.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PlantryIdentityDbContext>("db", tags: ["ready"]);

// Persist the DataProtection key ring to a fixed path mounted as a named Docker volume.
// Without explicit PersistKeysToFileSystem, ASP.NET Core falls back to the home-relative
// default ($HOME/.aspnet/DataProtection-Keys) and always logs warning [60] — even when
// that directory is mounted. An explicit repository suppresses the warning and ensures the
// key ring survives 'docker compose pull && up -d' (container recreation on update).
// SetApplicationName keeps the purpose string stable across image rebuilds so an existing
// ring remains valid. The /keys path matches the dp_keys volume mount in docker-compose.yml
// and docker-compose.prod.yml; for local dev the directory is created on first startup.
//
// ProtectKeysWithCertificate encrypts the XML key ring at rest so the keys cannot be used
// even if the dp_keys volume is exfiltrated.  The PFX is generated once by the dp-cert-init
// one-shot service on first start and stored in the dp_certs volume (/certs/dp.pfx).
// DP_CERT_PASSWORD (required in production) is the decryption passphrase.
// In non-production environments the certificate and encryption are skipped so local dev
// and the test host work without any extra setup.
var dpBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        builder.Configuration["DataProtection:KeyPath"] ?? "/keys"))
    .SetApplicationName("Plantry");

var certPath = builder.Configuration["DataProtection:CertPath"] ?? "/certs/dp.pfx";
var certPassword = builder.Configuration["DataProtection:CertPassword"]
    ?? builder.Configuration["DP_CERT_PASSWORD"];

// In Production, fail loudly if the cert or password is absent — a silent skip would boot
// with an unencrypted key ring and suppress neither the XmlKeyManager[35] warning nor the
// actual security gap.  Non-Production (local dev / test host) skips encryption gracefully
// so neither requires any extra setup.  Mirrors the Database:AppUserPassword guard above.
if (builder.Environment.IsProduction()
    && (string.IsNullOrWhiteSpace(certPassword) || !File.Exists(certPath)))
{
    throw new InvalidOperationException(
        $"DataProtection certificate is required in Production but was not available " +
        $"(certPath='{certPath}' exists={File.Exists(certPath)}, " +
        $"password set={!string.IsNullOrWhiteSpace(certPassword)}). " +
        "Set DP_CERT_PASSWORD and ensure dp-cert-init has run.");
}

if (!string.IsNullOrWhiteSpace(certPassword) && File.Exists(certPath))
{
    var dpCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
        certPath, certPassword,
        System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
    dpBuilder.ProtectKeysWithCertificate(dpCert);
}

// Session support — required for IPendingProposalStore store keys (P3-6a).
// Uses in-process distributed memory cache (single-server; no Redis needed for Phase 3).
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(opts =>
{
    opts.IdleTimeout = TimeSpan.FromHours(2);
    opts.Cookie.HttpOnly = true;
    opts.Cookie.IsEssential = true;
});

builder.Services.AddRazorPages(options =>
{
    // Explicit create-recipe route: the page-level route "/Recipes/{id:guid?}/Edit" only matches when
    // an id is present (ASP.NET Core does not collapse optional mid-path segments into a shorter URL).
    // Adding "/Recipes/New" as an alias routes the new-recipe form without an id binding, which causes
    // EditModel.Id to be null → create branch (J6).
    options.Conventions.AddPageRoute("/Recipes/Edit", "Recipes/New");
});

// The injected connection string is the database owner. At runtime the app connects as the
// non-superuser 'app_user' role so Postgres RLS policies actually apply (RLS, even FORCE,
// never applies to superusers/owners). The owner string is used here only to derive the
// app_user runtime connection (swap username/password). Migrations are handled externally:
// the Migrator resource in Aspire (dev) and the Plantry.Migrator container in compose (prod).
// See the InitialCatalogSchema / InitialIdentitySchema migrations and ADR-017.
var ownerConnStr = builder.Configuration.GetConnectionString("plantrydb")
    ?? "Host=localhost;Database=plantrydb;Username=postgres;Password=postgres";

// Production must supply the app_user password explicitly; every non-production
// environment (Development and the "Testing" host used by the L4 WebApplicationFactory
// suite) falls back to the well-known local role password.
var appUserPassword = builder.Configuration["Database:AppUserPassword"]
    ?? (builder.Environment.IsProduction()
        ? throw new InvalidOperationException("Database:AppUserPassword must be configured in production.")
        : "app_user_password");

var appUserConnStr = new NpgsqlConnectionStringBuilder(ownerConnStr)
{
    Username = "app_user",
    Password = appUserPassword,
}.ConnectionString;

// Ambient, request-scoped tenant + the interceptor that arms RLS (SET app.household_id) on the
// live connection for both DbContexts. Together with the EF query filters this gives
// defense-in-depth: app-layer filter AND database-enforced row-level security.
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<HouseholdRlsConnectionInterceptor>();

builder.Services.AddDbContext<PlantryIdentityDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Identity.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));

builder.Services.AddDbContext<CatalogDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Catalog.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));

builder.Services.AddDbContext<InventoryDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Inventory.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));

builder.Services.AddIdentity<AppUser, IdentityRole>(opts =>
    {
        opts.Password.RequireDigit = false;
        opts.Password.RequireLowercase = false;
        opts.Password.RequireNonAlphanumeric = false;
        opts.Password.RequireUppercase = false;
        opts.Password.RequiredLength = 8;
        opts.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<PlantryIdentityDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(opts =>
{
    opts.LoginPath = "/Account/Login";
    opts.LogoutPath = "/Account/Logout";
    opts.AccessDeniedPath = "/Account/AccessDenied";
});

builder.Services.AddScoped<IClock, SystemClock>();
builder.Services.AddScoped<IHouseholdRepository, HouseholdRepository>();
// Per-household "AI assistance" switch (plantry-qll2.1): one settings service backs both the read
// gate (IAiAssistanceGate — the single point of truth governed call sites query) and the
// /Settings/Ai write path. The setting lives on the Household aggregate (identity schema).
builder.Services.AddScoped<AiAssistanceSettingsService>();
builder.Services.AddScoped<IAiAssistanceGate>(sp => sp.GetRequiredService<AiAssistanceSettingsService>());

// plantry-m1u: cross-context ACL adapters + the domain-event dispatch machinery (dispatcher +
// interceptor pair + transactional buffer) are wired from the dedicated Plantry.Composition assembly
// (CompositionServiceCollectionExtensions) — "how bounded contexts are wired together" lives outside
// this web/UI host. The DbContext .AddInterceptors(...) calls below resolve the interceptors this
// registers. Two composition bindings deliberately stay in this host: the Identity read-port impl
// (just below — ASP.NET-coupled, must not enter Composition) and the feature-flagged IFlyerSource seam.
builder.Services.AddCrossContextAdapters();
// Identity read-port implementation backing the moved MealPlanning HouseholdMemberReaderAdapter.
// HouseholdDirectory is ASP.NET-Identity-coupled (UserManager<AppUser>), so it stays in the host and
// Plantry.Composition depends only on the Plantry.Identity.Application IHouseholdDirectory port.
builder.Services.AddScoped<IHouseholdDirectory, HouseholdDirectory>();

builder.Services.AddScoped<IReferenceDataSeeder, CatalogReferenceDataSeeder>();
builder.Services.AddScoped<IUnitRepository, UnitRepository>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<ILocationRepository, LocationRepository>();
builder.Services.AddScoped<IStoreRepository, StoreRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<ProductQueryService>();

// Inventory context
builder.Services.AddScoped<IProductStockRepository, ProductStockRepository>();
builder.Services.AddScoped<InventoryQueryService>();
// Per-household "expiring soon" horizon (plantry-5yhd): one settings service backs both the read
// port (IExpiringSoonHorizon, consumed by InventoryQueryService and the Recipes adapter) and the
// /Settings/Pantry write path.
builder.Services.AddScoped<IHouseholdInventorySettingsRepository, HouseholdInventorySettingsRepository>();
builder.Services.AddScoped<ExpiringSoonSettingsService>();
builder.Services.AddScoped<IExpiringSoonHorizon>(sp => sp.GetRequiredService<ExpiringSoonSettingsService>());
// Purchase-frequency read over the stock journal — feeds the Deals stock-up alerts (P5-10 / DL-O4).
builder.Services.AddScoped<IPurchaseJournalReader, PurchaseJournalReader>();
builder.Services.AddScoped<IProductConversionProvider, CatalogConversionProvider>();
builder.Services.AddScoped<ICatalogReadFacade, CatalogReadFacade>();
// ITakeStockReader/ITakeStockCatalogWriter adapters → Plantry.Composition (AddCrossContextAdapters).

// Pricing context
builder.Services.AddDbContext<PricingDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Pricing.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
builder.Services.AddScoped<IPriceObservationRepository, PriceObservationRepository>();
// IUnitPriceCalculator adapter → Plantry.Composition (AddCrossContextAdapters).
builder.Services.AddScoped<PricingQueries>();

// DM-16 part D: one-time backfill stamping store_id onto historical purchase observations recorded before
// the intake write-path resolved it. PurchaseStoreBackfill is the per-household unit of work (scoped);
// PurchaseStoreBackfillCycle arms tenancy per household and opens a fresh scope itself, so — like
// FlyerIngestionCycle — it is a singleton, driven only by the dev-only manual endpoint below (no worker,
// never at boot). See Pricing/PurchaseStoreBackfill*.cs.
builder.Services.AddScoped<PurchaseStoreBackfill>();
builder.Services.AddSingleton<PurchaseStoreBackfillCycle>();

// Intake context (hero AI receipt flow — ADR-007/ADR-010). The dispatch interceptor drains domain
// events (e.g. ImportSessionCommittedEvent) after a successful SaveChanges; the AI parser, the four
// cross-context port adapters, and the event handler are the seams ParseSessionCommand /
// CommitSessionCommand are constructed over.
// IDomainEventDispatcher + TransactionalDomainEventBuffer + the DomainEventDispatchInterceptor /
// DomainEventCommitDispatchInterceptor pair → Plantry.Composition (AddCrossContextAdapters). The
// DbContext .AddInterceptors(...) calls below resolve them. Event HANDLERS stay in this host:
builder.Services.AddScoped<IDomainEventHandler<ImportSessionCommittedEvent>, ImportSessionCommittedLogHandler>();
// GUARDRAIL (ADR-014): domain events dispatch on COMMIT with no transactional outbox. The dispatch
// interceptor pair (DomainEventDispatchInterceptor + DomainEventCommitDispatchInterceptor) makes dispatch
// transaction-aware (plantry-jvzk): a bare SaveChanges dispatches immediately post-commit, while events
// raised INSIDE an explicit multi-save transaction are buffered and dispatched only when that transaction
// commits — so a rolled-back transaction dispatches NOTHING (the pre-commit "phantom event on rollback"
// window is CLOSED). What remains latent is the OTHER window: a process crash AFTER commit but before a
// handler runs is still an at-most-once lost event — this is not an outbox. RecipeCookedEvent has no
// subscriber today, so that window is harmless. Before registering the FIRST RecipeCookedEvent handler
// here, either build the outbox or explicitly accept that handler as at-most-once — if it produces durable,
// reconcilable output, prefer self-reconciliation (the plantry-292 saga pattern) over a generic outbox.

builder.Services.AddDbContext<IntakeDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Intake.Infrastructure"))
        .AddInterceptors(
            sp.GetRequiredService<HouseholdRlsConnectionInterceptor>(),
            sp.GetRequiredService<DomainEventDispatchInterceptor>(),
            sp.GetRequiredService<DomainEventCommitDispatchInterceptor>()));
builder.Services.AddScoped<IImportSessionRepository, ImportSessionRepository>();
builder.Services.AddScoped<PendingReviewQuery>();

// Receipt-upload abuse gate (plantry-aij): per-household burst + daily rate limit over the upload POST
// handler. Singleton so its fixed-window counters persist across requests; limits are tunable via the
// Intake:UploadRateLimit config section (defaults 10/min + 100/day). The pre-buffer size cap and the
// magic-byte sniff are enforced on the page model itself (see Pages/Intake/Upload.cshtml.cs).
builder.Services.Configure<ReceiptUploadRateLimitOptions>(
    builder.Configuration.GetSection(ReceiptUploadRateLimitOptions.SectionName));
builder.Services.AddSingleton<ReceiptUploadRateLimiter>();

// Receipt image downscaling (plantry-v8vw): oversized uploads are auto-oriented, resized to a 2048px
// longest edge and re-encoded JPEG q85 before ParseSessionCommand — cutting AI token cost, latency, and
// stored image size with no OCR loss. Stateless (Magick.NET native codec) → singleton.
builder.Services.AddSingleton<IReceiptImagePreprocessor, ReceiptImagePreprocessor>();

// Recipes context (Phase 2). P2-1 adds domain behaviour, EF child-collection mapping, and the
// IRecipeRepository; P2-3a adds ICookEventRepository; later P2 steps add application services.
builder.Services.AddDbContext<RecipesDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Recipes.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
builder.Services.AddScoped<IRecipeRepository, RecipeRepository>();
builder.Services.AddScoped<ICookEventRepository, CookEventRepository>();
builder.Services.AddScoped<ITagRepository, TagRepository>();
builder.Services.AddScoped<IReferenceDataSeeder, RecipesReferenceDataSeeder>();

// Shopping context (P2-S). Mutable working-state context — items edited in place and hard-deleted
// on clear (shopping.md resolved call 2). ShoppingReferenceDataSeeder seeds one list per household.
builder.Services.AddDbContext<ShoppingDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Shopping.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
builder.Services.AddScoped<IShoppingListRepository, ShoppingListRepository>();
builder.Services.AddScoped<IReferenceDataSeeder, ShoppingReferenceDataSeeder>();

// Meal Planning context (Phase 3 / P3-0). MealPlanningReferenceDataSeeder seeds Breakfast/Lunch/Dinner
// default slots at household creation (DM-9). MealPlanningDbContext MUST be wired into RlsMiddleware
// (see Tenancy/RlsMiddleware.cs) — the known P3-0 gotcha (see also bd memory rls-middleware-...).
builder.Services.AddDbContext<MealPlanningDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.MealPlanning.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
builder.Services.AddScoped<IMealSlotConfigRepository, MealSlotConfigRepository>();
builder.Services.AddScoped<IUserPreferenceRepository, UserPreferenceRepository>();

// Deals context (Phase 5 / P5-0). DbContext + schema (P5-0); store subscriptions + §7e management (P5-2).
// DealsDbContext MUST be wired into RlsMiddleware (see Tenancy/RlsMiddleware.cs) — the known P2-0/P3-0
// gotcha: omit it and every Deals query filter returns nothing while writes silently succeed.
// DealConfirmed/DealRejected (P5-5) and FlyerImportedEvent (P5-6) dispatch through the same interceptor pair
// as Intake — no subscriber today (latent, like RecipeCookedEvent; see the ADR-014 guardrail above). The
// dispatch interceptor drains + clears the buffered events on every save so they can't accumulate; because
// FlyerImportedEvent is raised inside IngestFlyer's explicit two-save materialization transaction, the
// transaction-aware pair holds it until COMMIT so an aborted import (rollback) fires no phantom event
// (plantry-jvzk).
builder.Services.AddDbContext<DealsDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Deals.Infrastructure"))
        .AddInterceptors(
            sp.GetRequiredService<HouseholdRlsConnectionInterceptor>(),
            sp.GetRequiredService<DomainEventDispatchInterceptor>(),
            sp.GetRequiredService<DomainEventCommitDispatchInterceptor>()));

// Deals — P5-2 store subscriptions + §7e (DJ1). IStoreSubscriptionRepository is the first Deals repo.
// ICatalogStoreReader/Writer are ACL ports onto Catalog's store reference data (DM-16) — the Web adapters
// implement them over Catalog's IStoreRepository / EnsureStoreCommand so Deals never touches CatalogDbContext
// (ADR-010/DM-3).
builder.Services.AddScoped<IStoreSubscriptionRepository, StoreSubscriptionRepository>();
// ICatalogStoreReader/ICatalogStoreWriter adapters → Plantry.Composition (AddCrossContextAdapters).

// Deals — P5-5 confirm/reject orchestration (DJ4). The Deal + DealMatchMemory repos, the Pricing observation
// writer (deal-sourced observation over P5-P's RecordObservationCommand; Deals never touches PricingDbContext),
// and the Catalog product-existence check (ADR-010/DM-3).
builder.Services.AddScoped<IDealRepository, DealRepository>();
builder.Services.AddScoped<IDealMatchMemoryRepository, DealMatchMemoryRepository>();
// IPriceObservationWriter + Deals ICatalogProductReader adapters → Plantry.Composition (AddCrossContextAdapters).
builder.Services.AddScoped<ConfirmDeal>();
builder.Services.AddScoped<RejectDeal>();

// Deals — P5-7 BrowseDeals read side + Deals page (DJ3). Read-only over the Deal aggregate + the clock;
// nothing stored. The active/pending partition is recomputed per request (DD7/DD14), names resolved via
// the batch Catalog/store ports (no N+1).
builder.Services.AddScoped<BrowseDeals>();

// Deals — P5-8 review queue (DJ4). ReviewDeals is the review-form read side (pending queue + single-deal
// correction lookup); the verbs reuse P5-5's ConfirmDeal/RejectDeal registered above. Inline product
// create in the review page runs over Catalog's CreateProductCommand (Web composition root).
builder.Services.AddScoped<ReviewDeals>();

// Deals — P5-10 stock-up alerts (DJ5). StockUpAlerts intersects an active-deal partition the caller supplies
// (the Deals page's single BrowseDeals read, ADR-010) with Inventory's purchase-journal frequency (IPurchaseFrequencyReader over InventoryQueryService,
// DL-O4); "Add to list" reuses the P2-4 Shopping AddItems seam via a Deals-side writer port (DM-18). Both
// adapters live in Web so Plantry.Deals keeps its → SharedKernel-only dependency.
builder.Services.AddScoped<StockUpAlerts>();
// IPurchaseFrequencyReader + IDealShoppingListWriter adapters → Plantry.Composition (AddCrossContextAdapters).

// Deals — P5-6 IngestFlyer worker (DJ2). IngestFlyer is the per-household unit of work (pull → dedup →
// normalize → match → materialize → auto-confirm); IFlyerImportRepository is the new dedup/provenance
// repo. FlyerIngestionCycle reproduces RlsMiddleware's tenancy arming with no HTTP request — cross-tenant
// household enumeration, then a fresh armed scope per household. FlyerIngestionWorker is the app's first
// BackgroundService, driving the cycle daily (locked cadence). See Deals/FlyerIngestion*.cs.
builder.Services.AddScoped<IFlyerImportRepository, FlyerImportRepository>();
builder.Services.AddScoped<IngestFlyer>();
builder.Services.Configure<FlyerIngestionOptions>(builder.Configuration.GetSection(FlyerIngestionOptions.SectionName));
// Singleton: it owns no per-request state and opens a fresh DI scope per household itself, so it is safe
// to inject into the singleton hosted worker (a scoped registration would fault at root resolution).
builder.Services.AddSingleton<FlyerIngestionCycle>();
builder.Services.AddHostedService<FlyerIngestionWorker>();

// Generic in-process fire-and-forget work queue (plantry-qll2.4): a request can enqueue post-response work
// (the async ai_suggested conversion seed) that runs on QueuedHostedService's single drain loop, each item
// in its own fresh DI scope with tenancy armed by the item. Singleton queue shared by producers + consumer.
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<QueuedHostedService>();

// IFlyerSource is the untrusted Flipp seam (D1). Production wires the real Flipp adapter (P5-3): a typed
// HttpClient (base URL + locale + browser UA from the Deals:Flipp config; standard resilience — timeout +
// retry — applied to every HttpClient by ServiceDefaults) mapping raw Flipp payloads to RawDeal/DirectoryMerchant.
// The P5-2 canned StubFlyerSourceAdapter is kept as a deterministic seam behind Deals:UseStubFlyerSource so
// E2E / L4 fragment tests exercise the §7e journey with no live Flipp call (mirrors the AI:UseFakeParser seam).
builder.Services.Configure<FlippOptions>(builder.Configuration.GetSection(FlippOptions.SectionName));
if (builder.Configuration.GetValue<bool>("Deals:UseStubFlyerSource"))
{
    builder.Services.AddScoped<IFlyerSource, StubFlyerSourceAdapter>();
}
else
{
    builder.Services.AddHttpClient<IFlyerSource, FlyerSource>(client =>
    {
        var flipp = builder.Configuration.GetSection(FlippOptions.SectionName).Get<FlippOptions>() ?? new FlippOptions();
        var baseUrl = flipp.BaseUrl.EndsWith('/') ? flipp.BaseUrl : flipp.BaseUrl + "/";
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(flipp.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    });
}

// IDealMatcher is the untrusted stage-2 AI match (DJ2 step 4, ADR-007) — the deal twin of
// GeminiReceiptParser. It consumes the same global AiOptions/ChatClient as Intake/MealPlanning (no
// per-household key; DM-7 unbuilt). DealMatcher builds a ChatClient at construction, which needs a
// non-empty key, so with no key configured we register DisabledDealMatcher (soft-fails to Unmatched)
// so a keyless dev/E2E host still resolves the port the P5-6 worker will consume.
if (string.IsNullOrWhiteSpace(builder.Configuration[$"{AiOptions.SectionName}:ApiKey"]))
    builder.Services.AddScoped<IDealMatcher, DisabledDealMatcher>();
else
    builder.Services.AddScoped<IDealMatcher, DealMatcher>();

builder.Services.AddScoped<ManageSubscriptions>();
builder.Services.AddScoped<ManageSlotsService>();
builder.Services.AddScoped<IReferenceDataSeeder, MealPlanningReferenceDataSeeder>();

// Meal Planning → Recipes / Identity anti-corruption adapters (P3-2, plantry-e78).
// TagReaderAdapter supplies grouped tag vocabulary from Recipes; HouseholdMemberReaderAdapter
// supplies household member display facts from Identity. SetPreferences orchestrates the
// lazy-create aggregate and stance mutations.
// ITagReader + IHouseholdMemberReader adapters → Plantry.Composition (AddCrossContextAdapters).
builder.Services.AddScoped<SetPreferences>();

// Meal Planning — P3-3 week grid services (plantry-7oy).
// IMealPlanRepository manages the MealPlan aggregate lifetime (find-or-create by week).
// IRecipeReadModel / IMealPlanCatalogProductReader are ACL ports over the Recipes and Catalog
// bounded contexts — MealPlanning.Application never takes a direct EF dependency on either context.
// MealConstraintResolver is a stateless domain service; AssignMealService / MoveMealService
// are the application-layer orchestrators for the two write paths.
builder.Services.AddScoped<IMealPlanRepository, MealPlanRepository>();
// IRecipeReadModel + IMealPlanCatalogProductReader adapters → Plantry.Composition (AddCrossContextAdapters).
builder.Services.AddScoped<MealConstraintResolver>();
builder.Services.AddScoped<AssignMealService>();
builder.Services.AddScoped<MoveMealService>();

// Meal Planning — P3-4 roll-up + Shop for the week (plantry-ux2).
// IMealPlanStockReader / IMealPlanPriceReader are MealPlanning-owned ACL ports onto the same
// Inventory / Pricing stack used by Recipes — separate interface copies per context (DM-3).
// IMealPlanShoppingWriter wraps Shopping's AddItemCommand with source="meal_plan" (DM-18).
// PlanFulfillmentService / PlanCostingService are stateless domain services that roll up
// Recipes' enrichment across a meal's dishes — MealPlanning never recomputes these (domain-model §1).
// IMealPlanStockReader + IMealPlanPriceReader + IMealPlanShoppingWriter adapters → Plantry.Composition (AddCrossContextAdapters).
builder.Services.AddScoped<PlanFulfillmentService>();
builder.Services.AddScoped<PlanCostingService>();
builder.Services.AddScoped<ShopForWeekService>();

// Meal Planning — P3-5 Plan insights (plantry-6si).
// IMealPlanExpiringStockReader is the insights-specific ACL port onto Inventory; adapter is in Web.
// PlanInsightsService is a stateless read-side domain service recomputed on every page load.
// IMealPlanExpiringStockReader + the MealPlanning IExpiringSoonHorizonReader adapter →
// Plantry.Composition (AddCrossContextAdapters).
builder.Services.AddScoped<PlanInsightsService>();

// Meal Planning — ADR-021 cross-schema read model (plantry-nz3u.1).
// MealPlanWeekReadModel loads all raw inputs for a week's meals in a small, flat set of
// raw SQL queries over an RLS-armed Npgsql connection. Lives in Plantry.Web (the composition
// root) and injects the app_user connection string directly — no EF context, no per-context
// HasQueryFilter — relying solely on Postgres RLS policies (ADR-008) for tenant isolation.
// Registered as Scoped so the ITenantContext is request-scoped and consistent with EF contexts.
builder.Services.AddScoped<IMealPlanWeekReadModel>(sp =>
    new MealPlanWeekReadModel(
        appUserConnStr,
        sp.GetRequiredService<ITenantContext>()));

// Meal Planning — P3-6a AI generate plan (plantry-o0z).
// GeneratePlanService orchestrates slot discovery, constraint resolution, candidate loading,
// IMealPlanner call (untrusted), ProposalAcl validation, and IPendingProposalStore staging.
// AcceptProposalService handles user acceptance/rejection of staged proposals.
// IPendingProposalStore is keyed by {householdId}_{weekStart}_{sessionId} (session must be wired above).
builder.Services.AddScoped<GeneratePlanService>();
builder.Services.AddScoped<AcceptProposalService>();
builder.Services.AddScoped<IPendingProposalStore, DistributedCachePendingProposalStore>();

// Meal Planning — persisted planning settings (plantry-so5.3).
// HouseholdPlanningSettings (household default budget/weights) + WeekPlanningOverride (per-week override).
// SetPlanningSettingsService upserts overrides and returns resolved values.
builder.Services.AddScoped<IHouseholdPlanningSettingsRepository, HouseholdPlanningSettingsRepository>();
builder.Services.AddScoped<IWeekPlanningOverrideRepository, WeekPlanningOverrideRepository>();
builder.Services.AddScoped<SetPlanningSettingsService>();

// IMealPlanner: FakeMealPlanner for test/no-key, real AI otherwise.
if (builder.Configuration.GetValue<bool>($"{MealPlanningAiOptions.SectionName}:UseFakePlanner")
    || string.IsNullOrWhiteSpace(builder.Configuration[$"{AiOptions.SectionName}:ApiKey"]))
    builder.Services.AddScoped<IMealPlanner, FakeMealPlanner>();
else
    builder.Services.AddScoped<IMealPlanner, MealPlannerAiService>();

// Shopping ACL adapters → Plantry.Composition (AddCrossContextAdapters): IShoppingCatalogReader (→ Catalog,
// P2-Sc), IShoppingPantryReader (→ Inventory, plantry-juh), IShoppingRecipeReader (→ Recipes, plantry-26g),
// IShoppingMealPlanReader + IShoppingDealAttributionReader (attribution lines, plantry-jwyb), and
// IShoppingDealReader (→ Pricing cheapest-active-deal badge, P5-9). All keep Shopping.Application off the
// other contexts' EF contexts (ADR-002 / ADR-010 / Gate 2).
builder.Services.AddScoped<ShoppingListQueryService>();
builder.Services.AddScoped<PantrySuggestionService>();

// Recipes → Catalog anti-corruption adapters (P2-1b, recipes-domain-model.md §8). The Port +
// Web-adapter seam: Recipes.Application owns the interfaces, these implement them over Catalog's
// repositories/commands and pure UnitConverter, so the Recipes projects stay → SharedKernel only.
// Recipes ICatalogProductReader + ICatalogWriter + IUnitConverter adapters → Plantry.Composition (AddCrossContextAdapters).

// Recipes → Inventory anti-corruption adapters (P2-2a / P2-3b, recipes-domain-model.md §8).
// Read port supplies FulfillmentService with live stock snapshots (available qty + soonest expiry).
// Write port (IInventoryConsumer) lets the Cook flow decrement the pantry via Inventory's single
// Consume primitive without the Recipes context touching Inventory tables directly (ADR-011).
// IInventoryStockReader + IInventoryConsumer + the Recipes IExpiringSoonHorizonReader adapters →
// Plantry.Composition (AddCrossContextAdapters).

// Recipes → Pricing IPriceReader adapter → Plantry.Composition (AddCrossContextAdapters): supplies
// CostingService with the latest PriceObservation per product from the Pricing context (P2-2b).

// Recipe domain services (P2-2a/P2-2b). Both are pure domain computations over their ports.
builder.Services.AddScoped<FulfillmentService>();
builder.Services.AddScoped<CostingService>();

// Recipe authoring application service (P2-1c, recipes-domain-model.md §7) — orchestrates create/edit
// over the Catalog ports + the recipe/tag repositories. Consumed by the P2-1d editor page.
builder.Services.AddScoped<AuthorRecipe>();

// Tag management application service (plantry-7ju). Drives the /Settings/Tags admin page:
// create/rename/set-category/archive/unarchive over the ITagRepository.
builder.Services.AddScoped<ManageTagsService>();

// Edit-moment AI tag suggestions (plantry-qll2.2). SuggestRecipeTags orchestrates the gate check +
// ingredient-name resolution + vocabulary load over the Recipes ACL ports; IRecipeTagSuggester is the
// untrusted LLM seam. IAiAssistanceGateReader adapter → Plantry.Composition (AddCrossContextAdapters).
// RecipeTagSuggester builds a ChatClient at construction (needs a non-empty key), so with no key
// configured we register DisabledRecipeTagSuggester (soft-fails to no suggestions) — mirrors DealMatcher
// — so a keyless dev/E2E host still resolves the port the editor consumes.
builder.Services.AddScoped<SuggestRecipeTags>();
if (string.IsNullOrWhiteSpace(builder.Configuration[$"{AiOptions.SectionName}:ApiKey"]))
    builder.Services.AddScoped<IRecipeTagSuggester, DisabledRecipeTagSuggester>();
else
    builder.Services.AddScoped<IRecipeTagSuggester, RecipeTagSuggester>();

// Edit-moment diet-tag contradiction nudge (plantry-qll2.3). DietTagNudgeService orchestrates the cheap
// ProductId-set guard + the deferred gate check + ingredient-name resolution over the Recipes ACL ports;
// IDietTagContradictionChecker is the untrusted LLM seam. It reuses the same IAiAssistanceGateReader adapter
// (Plantry.Composition) as qll2.2. DietTagContradictionChecker builds a ChatClient at construction (needs a
// non-empty key), so with no key configured we register DisabledDietTagContradictionChecker (soft-fails to no
// nudge) — mirroring RecipeTagSuggester/DealMatcher — so a keyless dev/E2E host still resolves the port.
builder.Services.AddScoped<DietTagNudgeService>();
if (string.IsNullOrWhiteSpace(builder.Configuration[$"{AiOptions.SectionName}:ApiKey"]))
    builder.Services.AddScoped<IDietTagContradictionChecker, DisabledDietTagContradictionChecker>();
else
    builder.Services.AddScoped<IDietTagContradictionChecker, DietTagContradictionChecker>();

// Edit-moment AI unit-conversion resolution (plantry-qll2.4, ADR-022). When the household AI toggle is on
// and a real inferrer is configured, a recipe saved with a cross-dimension unit gap fires a fire-and-forget
// background seed of an ai_suggested ProductConversion (RecipeConversionSeedTrigger enqueues onto the
// shared IBackgroundTaskQueue; RecipeConversionSeeder does the Catalog re-check + AddConversion inside a
// fresh armed scope). IngredientConversionInferrer builds a ChatClient at construction (needs a non-empty
// key), so with no key configured we register DisabledIngredientConversionInferrer (IsAvailable=false →
// the editor keeps today's manual C10 prompt) — mirroring RecipeTagSuggester/DietTagContradictionChecker.
if (string.IsNullOrWhiteSpace(builder.Configuration[$"{AiOptions.SectionName}:ApiKey"]))
    builder.Services.AddScoped<IIngredientConversionInferrer, DisabledIngredientConversionInferrer>();
else
    builder.Services.AddScoped<IIngredientConversionInferrer, IngredientConversionInferrer>();
builder.Services.AddScoped<RecipeConversionSeeder>();
builder.Services.AddScoped<RecipeConversionSeedTrigger>();
// One-shot rollout backfill (dev-only endpoint below) — a singleton like the other backfill cycles; it
// opens its own per-household scopes and never runs at boot.
builder.Services.AddSingleton<RecipeConversionBackfillCycle>();

// Recipe browse query (P2-2c, J1/J2). Assembles the browse view model: lean recipe list + live
// fulfillment/cost per recipe + filter/sort in the application layer.
builder.Services.AddScoped<BrowseRecipesQuery>();

// Reconcile-pending-cooks service (P2-3d / plantry-292c). Re-drives Pending consume lines left by
// interrupted cooks — called opportunistically at CookRecipe entry and on-demand via the dedicated
// endpoint. No background poller (ADR-010 defers infra until needed).
builder.Services.AddScoped<ReconcilePendingCooks>();

// Deferred unit-gap convergence (plantry-qll2.6). ApplyDeferredUnitGaps retro-applies DeferredUnitGap
// consume lines once a conversion for their (product, unit-pair) lands — called synchronously from the
// Composition layer after a conversion is added/promoted (manual product-detail add/promote + the qll2.4
// AI-seed trigger) and opportunistically at CookRecipe entry as a self-heal. VoidDeferredUnitGapLines
// supersedes them when an absolute Take Stock count observes the product's real level. Both re-use the
// idempotent IInventoryConsumer path and the ICookEventRepository — same shape as ReconcilePendingCooks.
builder.Services.AddScoped<ApplyDeferredUnitGaps>();
builder.Services.AddScoped<VoidDeferredUnitGapLines>();

// Cook-a-recipe application service (P2-3c, recipes-domain-model.md §7). Drives the J4 cook flow:
// ServingsScale + variant resolution (C7/C11) + atomic consume + cook event write (§7/§8).
// Runs an opportunistic reconciliation sweep (292c) at entry before starting the new cook.
builder.Services.AddScoped<CookRecipe>();

// Recipes → Shopping anti-corruption write adapter (P2-4a, recipes-domain-model.md §8 IShoppingListWriter).
// ShoppingListWriterAdapter (implements IShoppingListWriter over Shopping's SyncSourceContributionCommand,
// stamping source=recipe + source_ref=recipeId; plantry-gsj / DM-18) → Plantry.Composition (AddCrossContextAdapters).

// Add-missing-to-shopping-list application service (P2-4a, recipes-domain-model.md §7, J5).
// Computes a fresh FulfillmentResult at the displayed servings, takes Missing lines (excluding untracked),
// scales quantities, and calls IShoppingListWriter.AddItems(source=recipe, source_ref=recipeId).
builder.Services.AddScoped<AddMissingToShoppingList>();

// Add-all-ingredients-to-shopping-list application service (plantry-s1z).
// Emits every quantity-bearing, stock-tracked (track_stock=true) ingredient for a recipe with
// Source=Recipe+SourceRef=recipeId. Distinct from AddMissingToShoppingList — does not filter by
// stock level, but does exclude untracked staples via ICatalogProductReader (C12, plantry-yukq).
builder.Services.AddScoped<AddIngredientsToShoppingList>();

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));

// Grocy import pipeline (plantry-zcw.1). GrocyClient (typed HttpClient) + ExtractCommand
// for the Extract stage. Config from "Grocy" section (user secrets in dev, env vars in prod).
builder.Services.AddGrocyImport(builder.Configuration);

// The real Gemini parser is the production default. Three deterministic alternatives replace it:
//  • AI:UseSampleParser=true → SampleReceiptParser, a real scanned receipt for local UI iteration (dev only);
//  • AI:UseFakeParser=true   → FakeReceiptParser, the fixed E2E journey fixture (set only by the E2E AppHost).
//  • no AI:ApiKey configured → DisabledReceiptParser, lets the app start with a locked-feature UI instead of crashing.
// Sample takes precedence over fake. Never enable either seam outside dev/test.
if (builder.Configuration.GetValue<bool>($"{IntakeAiOptions.SectionName}:UseSampleParser"))
    builder.Services.AddScoped<IReceiptParser, SampleReceiptParser>();
else if (builder.Configuration.GetValue<bool>($"{IntakeAiOptions.SectionName}:UseFakeParser"))
    builder.Services.AddScoped<IReceiptParser, FakeReceiptParser>();
else if (string.IsNullOrWhiteSpace(builder.Configuration[$"{AiOptions.SectionName}:ApiKey"]))
    builder.Services.AddScoped<IReceiptParser, DisabledReceiptParser>();
else
    builder.Services.AddScoped<IReceiptParser, GeminiReceiptParser>();
builder.Services.AddScoped<ICatalogHintProvider, CatalogHintProvider>();
// Intake cross-context write adapters → Plantry.Composition (AddCrossContextAdapters): ICreateProductPort,
// IAddStockPort, IRecordPricePort, IEnsurePurchaseStorePort (receipt merchant → catalog.store on commit,
// DM-16), and ISeedConversionPort. All keep Intake off the other contexts' EF contexts (ADR-010).
builder.Services.AddScoped<IReviewReferenceDataProvider, ReviewReferenceDataProvider>();

if (builder.Environment.IsDevelopment())
    builder.Services.AddScoped<FakeDataSeeder>();

// Registry of dev-only endpoints, populated by MapDevPost as routes are mapped and rendered by the
// /Dev/Endpoints reference page. Registered unconditionally (harmless when empty) so the page model
// can always resolve it; the endpoints themselves are still only mapped in Development below.
builder.Services.AddSingleton<DevEndpointRegistry>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseDevPagesGate();
if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.UseRls();

if (app.Environment.IsDevelopment())
{
    // Dev-only endpoints, each mapped via MapDevPost so it auto-appears on the /Dev/Endpoints
    // reference page (registry-sourced — a new endpoint added through the helper needs no page edit).
    // All are gated by DevPagesGateMiddleware above (returns 404 outside Development).
    app.MapDevPost("/Dev/Seed", async (FakeDataSeeder seeder, CancellationToken ct) =>
    {
        await seeder.SeedAsync(ct);
        return Results.Ok();
    }, "Additively seed fake demo data (products, recipes, inventory) without wiping what's already there.");

    app.MapDevPost("/Dev/Reset", async (FakeDataSeeder seeder, CancellationToken ct) =>
    {
        await seeder.ResetAndSeedAsync(ct);
        return Results.Ok();
    }, "Wipe ALL data, then reseed the fake demo data set from scratch.", destructive: true);

    // Deals §7e "pull now": drive one full flyer-ingestion sweep on demand instead of waiting for the
    // daily timer (P5-6). Dev-only (gated by DevPagesGateMiddleware); the sweep arms tenancy per household.
    app.MapDevPost("/Dev/Deals/PullNow", async (FlyerIngestionCycle cycle, CancellationToken ct) =>
    {
        await cycle.RunAsync(ct);
        return Results.Ok();
    }, "Drive one full flyer-ingestion sweep on demand (Deals §7e) instead of waiting for the daily timer.");

    // DM-16 part D "backfill now": drive the one-time store-id backfill across every household on demand
    // (the sweep is not scheduled and never runs at boot). Dev-only (gated by DevPagesGateMiddleware);
    // idempotent + re-runnable, so re-triggering is safe. Mirrors /Dev/Deals/PullNow.
    app.MapDevPost("/Dev/Pricing/BackfillPurchaseStores", async (PurchaseStoreBackfillCycle cycle, CancellationToken ct) =>
    {
        await cycle.RunAsync(ct);
        return Results.Ok();
    }, "Run the one-time purchase-store-id backfill across every household (DM-16 part D; idempotent, re-runnable).");

    // plantry-qll2.4 "backfill now": drive the one-shot AI-suggested conversion backfill across every
    // household on demand — scans existing recipes for cross-dimension unit gaps and seeds ai_suggested
    // conversions the same way the post-save trigger does (ADR-022). Kept OUT of the save path (the
    // ticket's constraint); idempotent + re-runnable (the seeder skips already-bridged pairs). Mirrors
    // /Dev/Deals/PullNow. Seeds only when a real AI inferrer is configured; otherwise a harmless no-op.
    app.MapDevPost("/Dev/Recipes/BackfillConversions", async (RecipeConversionBackfillCycle cycle, CancellationToken ct) =>
    {
        await cycle.RunAsync(ct);
        return Results.Ok();
    }, "Seed AI-suggested conversions for existing recipes' cross-dimension unit gaps across every household (plantry-qll2.4; idempotent, re-runnable).");
}

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapDefaultEndpoints();

app.Run();
