using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Web;
using Plantry.Ai.Infrastructure;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.Pantry.Infrastructure;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Market.Infrastructure;
using Plantry.Composition;
using Plantry.Composition.Infrastructure;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Intake.Infrastructure;
using Plantry.Migration.Grocy;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Planning.Infrastructure;
using Plantry.Web.MealPlanning;
using Plantry.Web.Pages.Today;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Background;
using Plantry.Web.Deals;
using Plantry.Web.Dev;
using Plantry.Web.Housekeeping;
using Plantry.Web.Intake;
using Plantry.Web.Pricing;
using Plantry.Web.Recipes;
using Plantry.Web.Shopping;
using Plantry.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// Money is rendered culture-free through MoneyDisplay (plantry-2x6e.2): a deterministic ISO→symbol map and
// integer-minor-unit formatting, so the host/container locale can never turn a currency symbol into the '¤'
// placeholder (the plantry-xtmt bug). No process-wide display-culture pin is needed — and none is set here, so
// the fix cannot silently regress if a call site is ever added that forgets to route through MoneyDisplay.

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

    // Take Stock's index carries a deliberate kebab-case route override (@page "/pantry/take-stock"),
    // which REPLACES the folder-convention route. That leaves the PascalCase folder URL /Pantry/TakeStock
    // — the path a visitor (or an audit tool) naturally guesses from the folder structure — returning a
    // hard 404 (plantry-w427). Alias the conventional path onto the same page so it resolves instead of
    // dead-ending; the canonical kebab URL the nav links to (/pantry/take-stock) is unchanged.
    options.Conventions.AddPageRoute("/Pantry/TakeStock/Index", "Pantry/TakeStock");
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

// Pantry context (Catalog + Inventory, unified into one DbContext — ADR-024 / plantry-g3da.10).
builder.Services.AddDbContext<PantryDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Pantry.Infrastructure"))
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

if (builder.Environment.IsEnvironment("Testing")
    && DateTimeOffset.TryParse(builder.Configuration["Testing:FixedUtcNow"], out var fixedUtcNow))
{
    builder.Services.AddScoped<IClock>(_ => new ConfiguredClock(fixedUtcNow));
}
else
{
    builder.Services.AddScoped<IClock, SystemClock>();
}
builder.Services.AddScoped<IHouseholdRepository, HouseholdRepository>();
// Per-household "AI assistance" switch (plantry-qll2.1): one settings service backs both the read
// gate (IAiAssistanceGate — the single point of truth governed call sites query) and the
// /Settings/Ai write path. The setting lives on the Household aggregate (identity schema).
builder.Services.AddScoped<AiAssistanceSettingsService>();
builder.Services.AddScoped<IAiAssistanceGate>(sp => sp.GetRequiredService<AiAssistanceSettingsService>());
// Per-household display currency (plantry-2x6e.1): one settings service backs both the read source
// (IDisplayCurrency — budget writers stamp it instead of hardcoded "USD") and the /Settings/Currency
// write path. Lives on the Household aggregate (identity schema).
builder.Services.AddScoped<DisplayCurrencyService>();
builder.Services.AddScoped<IDisplayCurrency>(sp => sp.GetRequiredService<DisplayCurrencyService>());
// Per-request cache over IDisplayCurrency (plantry-2x6e.2): the presentation edge resolves the household
// display currency once per request (one DB read) and threads it onto view models via MoneyDisplay.
// Registered via AddCrossContextAdapters (Plantry.Composition), not here (plantry-47tc, absorbing
// plantry-x9vm) — see CompositionServiceCollectionExtensions for the Scoped/tenant-load-bearing rationale.
// Per-household freeze/thaw expiry defaults (plantry-hh1f): one settings service backs both the read
// source (IHouseholdExpiryDefaults — Catalog's IHouseholdExpiryDefaultsReader ACL adapter, registered
// below via AddCrossContextAdapters, delegates to this) and the future /Settings/Expiry write path
// (plantry-qckx). Lives on the Household aggregate (identity schema).
builder.Services.AddScoped<HouseholdExpiryDefaultsService>();
builder.Services.AddScoped<IHouseholdExpiryDefaults>(sp => sp.GetRequiredService<HouseholdExpiryDefaultsService>());
// Household membership invites (plantry-00v1): issue/revoke run under the authenticated household;
// accept runs pre-auth and resolves the invite by its unique token (identity schema).
builder.Services.AddScoped<IHouseholdInviteRepository, HouseholdInviteRepository>();
builder.Services.AddScoped<HouseholdInviteService>();
// Atomic join-via-invite (plantry-bmfg): create user + stamp claim + accept invite in ONE transaction
// on the shared identity DbContext. Extracted from the Join page so it no longer orchestrates the saga.
builder.Services.AddScoped<JoinHouseholdCommand>();

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
// Household default storage location (plantry-iypo): one service backs both the read port
// (IHouseholdDefaultLocationReader, consumed by InventoryProducerAdapter's yield-placement fallback
// chain) and the /Settings/Pantry write path — same per-household settings row as the "expiring soon"
// horizon above.
builder.Services.AddScoped<HouseholdDefaultLocationService>();
builder.Services.AddScoped<IHouseholdDefaultLocationReader>(sp => sp.GetRequiredService<HouseholdDefaultLocationService>());
// Purchase-frequency read over the stock journal — feeds the Deals stock-up alerts (P5-10 / DL-O4).
builder.Services.AddScoped<IPurchaseJournalReader, PurchaseJournalReader>();
// Waste-journal read over the same stock journal — feeds the Today "did you know" stats widget
// (plantry-h9z9), same shape/rationale as IPurchaseJournalReader just above but for Discarded rows.
builder.Services.AddScoped<IWasteJournalReader, WasteJournalReader>();
// Batched journal-by-SourceRef read (plantry-0eut) — feeds the MealPlanning cook-status composition
// adapter's product-dish leg (Plantry.Composition, AddCrossContextAdapters). Inventory-only, so it is
// registered here like IPurchaseJournalReader rather than in the composition root.
builder.Services.AddScoped<IJournalEntriesBySourceRefReader, JournalEntriesBySourceRefReader>();
// CatalogConversionProvider / CatalogReadFacade now live in Plantry.Pantry.Application (ADR-024
// plantry-g3da.6 Pantry merge) rather than bridging Plantry.Web across two assemblies, but stay
// registered from the host like the other Pantry-only services above.
builder.Services.AddScoped<IProductConversionProvider, CatalogConversionProvider>();
builder.Services.AddScoped<ICatalogReadFacade, CatalogReadFacade>();
// ITakeStockReader/ITakeStockCatalogWriter adapters → Plantry.Composition (AddCrossContextAdapters).

// Market context (Pricing + Deals, unified into one DbContext — ADR-024 / plantry-g3da.7).
builder.Services.AddDbContext<MarketDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Market.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));

// Pricing context
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

// Intake context (hero AI receipt flow — ADR-007/ADR-010). The AI parser and the four cross-context
// port adapters are the seams ParseSessionCommand / CommitSessionCommand are constructed over.
builder.Services.AddDbContext<IntakeDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Intake.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
builder.Services.AddScoped<IImportSessionRepository, ImportSessionRepository>();
builder.Services.AddScoped<PendingReviewQuery>();
// Today stats widget (plantry-h9z9) — composes IWasteJournalReader + MealPlanStreakQuery, both
// registered above, into the rotating "did you know" fact + streak chips. Registered here (a
// Web-layer, Today-page-only composition) rather than in either bounded context, since it has
// exactly one consumer and crosses Pantry/Planning the same way IndexModel's other constructor
// dependencies already do.
builder.Services.AddScoped<TodayStatsService>();

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
// Per-user recipe ratings (plantry-zlwp.1) — the RecipeRating aggregate's repository.
builder.Services.AddScoped<IRecipeRatingRepository, RecipeRatingRepository>();
// Ingredient substitutions (plantry-aqpa.1) — the Substitution aggregate's repository + read seam.
builder.Services.AddScoped<ISubstitutionRepository, SubstitutionRepository>();
builder.Services.AddScoped<ISubstitutionReader, SubstitutionReader>();
builder.Services.AddScoped<RecipesReferenceDataSeeder>();
builder.Services.AddScoped<IReferenceDataSeeder>(sp => sp.GetRequiredService<RecipesReferenceDataSeeder>());
builder.Services.AddSingleton<RecipesReferenceDataRollout>();

// Planning context (Meal Planning + Shopping, ADR-024). A single PlanningDbContext spans both the
// shopping and meal_planning schemas (unified plantry-g3da.8) — the schemas themselves did not move
// or merge. PlanningDbContext MUST be wired into RlsMiddleware (see Tenancy/RlsMiddleware.cs) — the
// known P2-0/P3-0 gotcha: omit it and every Planning query filter returns nothing while writes
// silently succeed.
builder.Services.AddDbContext<PlanningDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Planning.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));

// Shopping half. Mutable working-state context (P2-S) — items edited in place and hard-deleted
// on clear (shopping.md resolved call 2). ShoppingReferenceDataSeeder seeds one list per household.
builder.Services.AddScoped<IShoppingListRepository, ShoppingListRepository>();
builder.Services.AddScoped<IReferenceDataSeeder, ShoppingReferenceDataSeeder>();

// Meal Planning half (Phase 3 / P3-0). MealPlanningReferenceDataSeeder seeds Breakfast/Lunch/Dinner
// default slots at household creation (DM-9).
builder.Services.AddScoped<IMealSlotConfigRepository, MealSlotConfigRepository>();
builder.Services.AddScoped<IUserPreferenceRepository, UserPreferenceRepository>();

// Deals context (Phase 5 / P5-0). Store subscriptions + §7e management (P5-2). MarketDbContext (shared
// with Pricing above) MUST be wired into RlsMiddleware (see Tenancy/RlsMiddleware.cs) — the known
// P2-0/P3-0 gotcha: omit it and every Market query filter returns nothing while writes silently succeed.

// Deals — P5-2 store subscriptions + §7e (DJ1). IStoreSubscriptionRepository is the first Deals repo.
// ICatalogStoreReader/Writer are ACL ports onto Catalog's store reference data (DM-16) — the Web adapters
// implement them over Catalog's IStoreRepository / EnsureStoreCommand so Deals never touches PantryDbContext
// (ADR-010/DM-3).
builder.Services.AddScoped<IStoreSubscriptionRepository, StoreSubscriptionRepository>();
// ICatalogStoreReader/ICatalogStoreWriter adapters → Plantry.Composition (AddCrossContextAdapters).

// Deals — P5-5 confirm/reject orchestration (DJ4). The Deal + DealMatchMemory repos, and the Catalog
// product-existence check (ADR-010/DM-3). ConfirmDeal now calls RecordObservationCommand directly
// (deal-sourced observation over P5-P's RecordObservationCommand) — both live in Plantry.Market since
// the Pricing/Deals merge (ADR-024), so the former cross-context observation-writer port is gone.
builder.Services.AddScoped<IDealRepository, DealRepository>();
builder.Services.AddScoped<IDealMatchMemoryRepository, DealMatchMemoryRepository>();
// Deals ICatalogProductReader adapter → Plantry.Composition (AddCrossContextAdapters).
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

// Deals — guided-flow presentation state (q9zr.13). Which step shows a deal ("demoted") and the step-1
// checkbox state are per household+session UI staging, NOT domain facts — held in a session-keyed
// IDistributedCache store (the vetted IPendingProposalStore pattern), never a column on the Deal aggregate.
// Page code reaches the store ONLY through DealsReviewFlowSession, which owns session-start + key derivation
// so the SO5.2 session-key invariant is structural (a handler cannot get a key without starting the session).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Plantry.Web.Pages.Deals.IReviewFlowStateStore, Plantry.Web.Pages.Deals.DistributedCacheReviewFlowStateStore>();
builder.Services.AddScoped<Plantry.Web.Pages.Deals.DealsReviewFlowSession>();

// Deals — the review queue builder (q9zr.3 + q9zr.13): the presentation orchestration (projection → flyer rail
// → handoff → step partition/resolution) lifted out of the ReviewModel page so the page stays thin handlers.
// Web-project presentation only (composes ReviewDeals + FlyerRail + the flow session), never in Plantry.Market.
builder.Services.AddScoped<Plantry.Web.Pages.Deals.DealReviewQueueBuilder>();

// Deals — P5-10 stock-up alerts (DJ5). StockUpAlerts intersects an active-deal partition the caller supplies
// (the Deals page's single BrowseDeals read, ADR-010) with Inventory's purchase-journal frequency (IPurchaseFrequencyReader over InventoryQueryService,
// DL-O4); "Add to list" reuses the P2-4 Shopping AddItems seam via a Deals-side writer port (DM-18). Both
// adapters live in Web so Plantry.Market keeps its → SharedKernel-only dependency.
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
builder.Services.AddSingleton<IFlyerIngestionCycle>(sp => sp.GetRequiredService<FlyerIngestionCycle>());
// TimeProvider seam (plantry-hdry): FlyerIngestionWorker's boot delay + PeriodicTimer are driven through
// this instead of the wall clock directly so FlyerIngestionWorkerTests can substitute FakeTimeProvider.
// TimeProvider.System is not auto-registered by the generic host, so it's wired explicitly here.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<FlyerIngestionWorker>();

// Generic in-process fire-and-forget work queue (plantry-qll2.4): a request can enqueue post-response work
// (the async ai_suggested conversion seed) that runs on QueuedHostedService's single drain loop, each item
// in its own fresh DI scope with tenancy armed by the item. Singleton queue shared by producers + consumer.
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<QueuedHostedService>();

// Housekeeping ("Tidy Up", tidy-up.md) — ADR-024 Phase A dissolved the Housekeeping bounded context;
// its 7 read-only detectors are now ADR-021 cross-schema read models living directly in Plantry.Web
// (the composition root). Dismissal — the only state Housekeeping ever owned — moved with them at
// first, then moved again (plantry-g3da.9, ADR-024 ratified option B) into
// Plantry.Composition.Infrastructure, the read layer's standing persistence home. Findings are
// computed live from other contexts' schemas via IStockFactsReadModel/IRecipeFactsReadModel (T4) and
// are never persisted — the DbContext + schema below back only the Dismissal tombstone (T5/T9).
// HousekeepingDbContext MUST be wired into RlsMiddleware (see Tenancy/RlsMiddleware.cs) — the known
// P2-0/P3-0 gotcha. No domain-event dispatch interceptors: Dismissal never raises domain events.
// MigrationsAssembly is "Plantry.Composition.Infrastructure" — the migrations live in that project's
// Migrations/ folder alongside HousekeepingDbContext (schema/table unchanged).
builder.Services.AddDbContext<HousekeepingDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Composition.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
builder.Services.AddScoped<IDismissalRepository, DismissalRepository>();
builder.Services.AddScoped<GetTidyUpPageQuery>();
builder.Services.AddScoped<DismissFindingCommand>();
builder.Services.AddScoped<RestoreFindingCommand>();

// Tidy Up's ADR-021 read models (ADR-024 Phase A) — raw SQL over an RLS-armed connection, shared by
// the two detector families: IStockFactsReadModel backs D1/D3/D4/D6 (stock+catalog facts),
// IRecipeFactsReadModel backs D2/D5/D7 (recipe+catalog+pricing facts). Registered Scoped, same
// rationale as IMealPlanWeekReadModel — ITenantContext is request-scoped.
builder.Services.AddScoped<IStockFactsReadModel>(sp =>
    new StockFactsReadModel(appUserConnStr, sp.GetRequiredService<ITenantContext>()));
builder.Services.AddScoped<IRecipeFactsReadModel>(sp =>
    new RecipeFactsReadModel(appUserConnStr, sp.GetRequiredService<ITenantContext>()));

// Tidy Up's 7 problem detectors (tidy-up.md T4/T8) — moved from Plantry.Composition's
// AddCrossContextAdapters (ADR-024 Phase A: Plantry.Composition must never reference Plantry.Web
// types, so this registration can only live here). Registered as IProblemDetector so
// GetTidyUpPageQuery discovers every implementation via IEnumerable<IProblemDetector>.
builder.Services.AddScoped<IProblemDetector, StockUnitUnconvertibleDetector>();
builder.Services.AddScoped<IProblemDetector, RecipeConversionGapDetector>();
builder.Services.AddScoped<IProblemDetector, StockExpiredDetector>();
builder.Services.AddScoped<IProblemDetector, StapleNoLowStockAlertDetector>();
builder.Services.AddScoped<IProblemDetector, RecipeIngredientNoPriceDetector>();
builder.Services.AddScoped<IProblemDetector, MixedIncompatibleUnitsDetector>();
builder.Services.AddScoped<IProblemDetector, RecipeLineUntrackedProductDetector>();
// Singleton (T6): the badge count must survive across requests/scopes with its own TTL; the query
// service and the dismiss/restore commands (all scoped) write/invalidate into it via the port.
// IClock is registered Scoped (a singleton cannot safely consume it via constructor injection —
// the classic captive-dependency trap), so this factory hands the cache SystemClock.Instance
// directly instead, mirroring WeekBagEnricher's singleton-context clock usage.
builder.Services.AddSingleton<ITidyUpBadgeCache>(_ => new TidyUpBadgeCache(SystemClock.Instance));
// IProblemDetector implementations (D1 + D2, v1 — T8) → Plantry.Composition (AddCrossContextAdapters).

// T6 proactive population (plantry-h0qq): the layout's badge read path never runs detectors, but a
// miss or stale (SWR-expired) cache read requests a single-flight background recompute here, and every
// process start warms every household up front — see TidyUpBadgeRefresher/TidyUpBadgeWarmup for the
// tenancy-arming and single-flight details. Singletons: the refresher's in-flight guard and the queue it
// wraps are both process-wide state; the warmup owns no per-request state and opens its own scope.
builder.Services.AddSingleton<TidyUpBadgeRefresher>();
builder.Services.AddSingleton<TidyUpBadgeWarmup>();

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
// ChunkSize (Deals:Matcher) controls how many memory-miss items ride in one completion (plantry-04ji);
// bound unconditionally so the setting applies whenever the real adapter is active.
builder.Services.Configure<DealMatcherOptions>(builder.Configuration.GetSection(DealMatcherOptions.SectionName));
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
// Consecutive weekly planning-streak read (plantry-h9z9) — feeds the Today stats widget's streak chip
// and rotating-fact pool; reads IMealPlanRepository.PlannedWeekStartsBeforeAsync in one scalar-only
// query, so it lives beside the other MealPlan-repository-backed services rather than as a standalone
// Web-layer computation.
builder.Services.AddScoped<MealPlanStreakQuery>();

// Meal Planning — P3-4 roll-up + Shop for the week (plantry-ux2).
// IMealPlanStockReader / IMealPlanPriceReader are MealPlanning-owned ACL ports onto the same
// Inventory / Pricing stack used by Recipes — separate interface copies per context (DM-3).
// ShopForWeekService calls Shopping's AddItemCommand with source=ItemSource.MealPlan directly — an
// intra-context call since the MealPlanning/Shopping merge into Plantry.Planning (ADR-024, plantry-g3da.5;
// formerly the IMealPlanShoppingWriter ACL port).
// PlanFulfillmentService / PlanCostingService are stateless domain services that roll up
// Recipes' enrichment across a meal's dishes — MealPlanning never recomputes these (domain-model §1).
// IMealPlanStockReader + IMealPlanPriceReader adapters → Plantry.Composition (AddCrossContextAdapters).
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
        sp.GetRequiredService<ITenantContext>(),
        sp.GetRequiredService<IClock>()));

// Meal Planning — deterministic generate plan.
// GeneratePlanService orchestrates slot discovery, constraint resolution, candidate loading,
// server-owned selection, ProposalAcl validation, and IPendingProposalStore staging.
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

// Shopping ACL adapters → Plantry.Composition (AddCrossContextAdapters): IShoppingCatalogReader (→ Catalog,
// P2-Sc), IShoppingPantryReader (→ Inventory, plantry-juh), IShoppingRecipeReader (→ Recipes, plantry-26g),
// IShoppingDealAttributionReader (attribution lines, plantry-jwyb), IShoppingDealReader (→ Pricing
// cheapest-active-deal badge, P5-9), and IShoppingPriceReader (→ Pricing raw price/qty/unit for the basket
// cost estimate, plantry-e016). MealPlan-source attribution labels resolve via
// IMealPlanRepository.FindSlotLabelsAsync directly — an intra-context call since the Planning merge
// (ADR-024, plantry-g3da.5; formerly the IShoppingMealPlanReader ACL port). All keep Shopping.Application
// off the other contexts' EF contexts (ADR-002 / ADR-010 / Gate 2).
// ShoppingBasketCostingService is a stateless domain service (mirrors PlanCostingService above) that rolls
// up the outstanding-basket estimate ShoppingListQueryService injects (plantry-e016).
builder.Services.AddScoped<ShoppingBasketCostingService>();
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
// over the Catalog ports + the recipe/tag repositories. Consumed by the P2-1d editor page. Its extracted
// phase cores (plantry-xgmb) — per-line product resolution and the R7/C10 conversion planner — are
// registered alongside it; both talk to Catalog only through the same anti-corruption ports.
builder.Services.AddScoped<IngredientLineResolver>();
builder.Services.AddScoped<ConversionGapPlanner>();
builder.Services.AddScoped<AuthorRecipe>();

// Archives a recipe with the N5 guard (recipe-composition.md D12): blocks while the recipe is included
// by another recipe's inclusion line, over the IRecipeRepository includers lookup.
builder.Services.AddScoped<ArchiveRecipe>();

// Recipe-composition expansion choke point (recipe-composition.md §4, D4). Resolves a recipe with its
// nested inclusions to a flat product-level line list; consumed by the Details inclusion preview
// (plantry-fqb0.3, its first consumer).
builder.Services.AddScoped<RecipeExpansionService>();

// Tag management application service (plantry-7ju). Drives the /Settings/Tags admin page:
// create/rename/set-category/archive/unarchive over the ITagRepository.
builder.Services.AddScoped<ManageTagsService>();
builder.Services.AddScoped<RecipeDiversityMetadataQuery>();

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

// Per-user recipe ratings (plantry-zlwp.1, epic plantry-zlwp): upsert/clear commands over the
// RecipeRating aggregate, and the per-member breakdown query for the rating popover/Details summary.
builder.Services.AddScoped<RateRecipe>();
builder.Services.AddScoped<ClearRecipeRating>();
builder.Services.AddScoped<GetRecipeRatingBreakdownQuery>();

// Product→recipes cross-context read (plantry-o0r8) — the Pantry product Detail page's "Recipes" section.
builder.Services.AddScoped<RecipesUsingProductQuery>();

// Cook line-drive protocol (plantry-dq16). The single owner of the anchor-first exception-to-status
// mapping (Applied / DeferredUnitGap / Shorted for consumes; Applied / Failed for produces) shared by
// CookRecipe and ReconcilePendingCooks, so a live cook and a reconcile can never classify the same
// failure differently. Wraps the IInventoryConsumer / IInventoryProducer adapters registered in
// Composition (AddCrossContextAdapters).
builder.Services.AddScoped<CookLineDriver>();

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

    // These three dev sweeps each run for many minutes (per-item AI matching / per-household backfills), so
    // they MUST NOT run inline on the request thread bound to HttpContext.RequestAborted (plantry-a2t8):
    // a client timeout/disconnect would silently cancel the sweep mid-run. Instead each queues onto the
    // shared IBackgroundTaskQueue and returns 202 immediately; QueuedHostedService then drains it under the
    // host lifetime token — exactly how the daily FlyerIngestionWorker tick and the qll2.4 conversion seed
    // run. The cycles are singletons that arm tenancy per household internally (no ambient HTTP request), so
    // the work item just resolves and runs them from the item's own scope. Re-triggering while one is still
    // queued/running is safe: all three are idempotent + re-runnable, and BackgroundTaskQueue's DropWrite
    // simply discards a duplicate enqueued onto a saturated queue.

    // Deals §7e "pull now": drive one full flyer-ingestion sweep on demand instead of waiting for the
    // daily timer (P5-6). Dev-only (gated by DevPagesGateMiddleware); the sweep arms tenancy per household.
    app.MapDevPost("/Dev/Deals/PullNow", async (IBackgroundTaskQueue queue) =>
    {
        await queue.EnqueueAsync(static (sp, ct) => sp.GetRequiredService<FlyerIngestionCycle>().RunAsync(ct));
        return Results.Accepted();
    }, "Queue one full flyer-ingestion sweep on demand (Deals §7e) instead of waiting for the daily timer; runs in the background (202).");

    // DM-16 part D "backfill now": drive the one-time store-id backfill across every household on demand
    // (the sweep is not scheduled and never runs at boot). Dev-only (gated by DevPagesGateMiddleware);
    // idempotent + re-runnable, so re-triggering is safe. Mirrors /Dev/Deals/PullNow.
    app.MapDevPost("/Dev/Pricing/BackfillPurchaseStores", async (IBackgroundTaskQueue queue) =>
    {
        await queue.EnqueueAsync(static (sp, ct) => sp.GetRequiredService<PurchaseStoreBackfillCycle>().RunAsync(ct));
        return Results.Accepted();
    }, "Queue the one-time purchase-store-id backfill across every household (DM-16 part D; idempotent, re-runnable); runs in the background (202).");

    // plantry-qll2.4 "backfill now": drive the one-shot AI-suggested conversion backfill across every
    // household on demand — scans existing recipes for cross-dimension unit gaps and seeds ai_suggested
    // conversions the same way the post-save trigger does (ADR-022). Kept OUT of the save path (the
    // ticket's constraint); idempotent + re-runnable (the seeder skips already-bridged pairs). Mirrors
    // /Dev/Deals/PullNow. Seeds only when a real AI inferrer is configured; otherwise a harmless no-op.
    app.MapDevPost("/Dev/Recipes/BackfillConversions", async (IBackgroundTaskQueue queue) =>
    {
        await queue.EnqueueAsync(static (sp, ct) => sp.GetRequiredService<RecipeConversionBackfillCycle>().RunAsync(ct));
        return Results.Accepted();
    }, "Queue AI-suggested conversions for existing recipes' cross-dimension unit gaps across every household (plantry-qll2.4; idempotent, re-runnable); runs in the background (202).");

}

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapDefaultEndpoints();

// T6 badge warmup (plantry-h0qq): fire-and-forget once the host finishes starting, so the badge cache
// is populated for every household before the first real request without delaying boot. Skipped under
// the "Testing" WAF host: each of the many WebApplicationFactory<Program> classes in Plantry.Tests.Web
// boots its own throwaway app instance, so an eager cross-household detector sweep on every one of them
// would only add DB load and log noise — those hosts still get correct badge behavior via the
// miss/stale-triggered TidyUpBadgeRefresher, and TidyUpBadgeWarmup itself is covered directly by
// TidyUpBadgeWarmupTests rather than by this hook firing.
if (!app.Environment.IsEnvironment("Testing"))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        _ = app.Services.GetRequiredService<RecipesReferenceDataRollout>()
            .RunAsync(app.Lifetime.ApplicationStopping);
        _ = app.Services.GetRequiredService<TidyUpBadgeWarmup>().RunAsync(app.Lifetime.ApplicationStopping);
    });
}

app.Run();
