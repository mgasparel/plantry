using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
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
builder.Services.AddScoped<IReferenceDataSeeder, CatalogReferenceDataSeeder>();
builder.Services.AddScoped<IUnitRepository, UnitRepository>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<ILocationRepository, LocationRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<ProductQueryService>();

// Inventory context
builder.Services.AddScoped<IProductStockRepository, ProductStockRepository>();
builder.Services.AddScoped<InventoryQueryService>();
builder.Services.AddScoped<IProductConversionProvider, CatalogConversionProvider>();
builder.Services.AddScoped<ICatalogReadFacade, CatalogReadFacade>();
builder.Services.AddScoped<ITakeStockReader, TakeStockReaderAdapter>();
builder.Services.AddScoped<ITakeStockCatalogWriter, TakeStockCatalogWriterAdapter>();

// Pricing context
builder.Services.AddDbContext<PricingDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Pricing.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
builder.Services.AddScoped<IPriceObservationRepository, PriceObservationRepository>();
builder.Services.AddScoped<IUnitPriceCalculator, UnitPriceCalculatorAdapter>();
builder.Services.AddScoped<PricingQueries>();

// Intake context (hero AI receipt flow — ADR-007/ADR-010). The dispatch interceptor drains domain
// events (e.g. ImportSessionCommittedEvent) after a successful SaveChanges; the AI parser, the four
// cross-context port adapters, and the event handler are the seams ParseSessionCommand /
// CommitSessionCommand are constructed over.
builder.Services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
builder.Services.AddScoped<DomainEventDispatchInterceptor>();
builder.Services.AddScoped<IDomainEventHandler<ImportSessionCommittedEvent>, ImportSessionCommittedLogHandler>();
// GUARDRAIL (ADR-014): domain events dispatch AFTER SaveChanges with no transactional outbox, so a
// dispatch failure is a lost-event window. RecipeCookedEvent has no subscriber today, so the window
// is latent. Before registering the FIRST RecipeCookedEvent handler here, either build the outbox or
// explicitly accept that handler as at-most-once — if it produces durable, reconcilable output,
// prefer self-reconciliation (the plantry-292 saga pattern) over a generic outbox.

builder.Services.AddDbContext<IntakeDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Intake.Infrastructure"))
        .AddInterceptors(
            sp.GetRequiredService<HouseholdRlsConnectionInterceptor>(),
            sp.GetRequiredService<DomainEventDispatchInterceptor>()));
builder.Services.AddScoped<IImportSessionRepository, ImportSessionRepository>();

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
builder.Services.AddScoped<ManageSlotsService>();
builder.Services.AddScoped<IReferenceDataSeeder, MealPlanningReferenceDataSeeder>();

// Meal Planning → Recipes / Identity anti-corruption adapters (P3-2, plantry-e78).
// TagReaderAdapter supplies grouped tag vocabulary from Recipes; HouseholdMemberReaderAdapter
// supplies household member display facts from Identity. SetPreferences orchestrates the
// lazy-create aggregate and stance mutations.
builder.Services.AddScoped<ITagReader, TagReaderAdapter>();
builder.Services.AddScoped<IHouseholdMemberReader, HouseholdMemberReaderAdapter>();
builder.Services.AddScoped<SetPreferences>();

// Meal Planning — P3-3 week grid services (plantry-7oy).
// IMealPlanRepository manages the MealPlan aggregate lifetime (find-or-create by week).
// IRecipeReadModel / IMealPlanCatalogProductReader are ACL ports over the Recipes and Catalog
// bounded contexts — MealPlanning.Application never takes a direct EF dependency on either context.
// MealConstraintResolver is a stateless domain service; AssignMealService / MoveMealService
// are the application-layer orchestrators for the two write paths.
builder.Services.AddScoped<IMealPlanRepository, MealPlanRepository>();
builder.Services.AddScoped<IRecipeReadModel, RecipeReadModelAdapter>();
builder.Services.AddScoped<IMealPlanCatalogProductReader, MealPlanCatalogProductReaderAdapter>();
builder.Services.AddScoped<MealConstraintResolver>();
builder.Services.AddScoped<AssignMealService>();
builder.Services.AddScoped<MoveMealService>();

// Meal Planning — P3-4 roll-up + Shop for the week (plantry-ux2).
// IMealPlanStockReader / IMealPlanPriceReader are MealPlanning-owned ACL ports onto the same
// Inventory / Pricing stack used by Recipes — separate interface copies per context (DM-3).
// IMealPlanShoppingWriter wraps Shopping's AddItemCommand with source="meal_plan" (DM-18).
// PlanFulfillmentService / PlanCostingService are stateless domain services that roll up
// Recipes' enrichment across a meal's dishes — MealPlanning never recomputes these (domain-model §1).
builder.Services.AddScoped<IMealPlanStockReader, MealPlanStockReaderAdapter>();
builder.Services.AddScoped<IMealPlanPriceReader, MealPlanPriceReaderAdapter>();
builder.Services.AddScoped<IMealPlanShoppingWriter, MealPlanShoppingWriterAdapter>();
builder.Services.AddScoped<PlanFulfillmentService>();
builder.Services.AddScoped<PlanCostingService>();
builder.Services.AddScoped<ShopForWeekService>();

// Meal Planning — P3-5 Plan insights (plantry-6si).
// IMealPlanExpiringStockReader is the insights-specific ACL port onto Inventory; adapter is in Web.
// PlanInsightsService is a stateless read-side domain service recomputed on every page load.
builder.Services.AddScoped<IMealPlanExpiringStockReader, MealPlanExpiringStockReaderAdapter>();
builder.Services.AddScoped<PlanInsightsService>();

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
if (builder.Configuration.GetValue<bool>($"{AiOptions.SectionName}:UseFakePlanner")
    || string.IsNullOrWhiteSpace(builder.Configuration[$"{AiOptions.SectionName}:ApiKey"]))
    builder.Services.AddScoped<IMealPlanner, FakeMealPlanner>();
else
    builder.Services.AddScoped<IMealPlanner, MealPlannerAiService>();

// Shopping → Catalog ACL adapter (P2-Sc). ShoppingCatalogReaderAdapter implements the Shopping
// anti-corruption port over Catalog repositories so Shopping.Application never takes a direct
// dependency on the Catalog EF context (Gate 2). ShoppingListQueryService assembles the read model.
builder.Services.AddScoped<IShoppingCatalogReader, ShoppingCatalogReaderAdapter>();

// Shopping → Inventory ACL adapter (plantry-juh). ShoppingPantryReaderAdapter implements the
// Shopping anti-corruption port over Inventory's persistence layer so Shopping.Application never
// reads Inventory tables directly (ADR-002). Supplies on-hand quantities and low flags for the
// item subline and search-dropdown stock hints.
builder.Services.AddScoped<IShoppingPantryReader, ShoppingPantryReaderAdapter>();

builder.Services.AddScoped<ShoppingListQueryService>();
builder.Services.AddScoped<PantrySuggestionService>();

// Recipes → Catalog anti-corruption adapters (P2-1b, recipes-domain-model.md §8). The Port +
// Web-adapter seam: Recipes.Application owns the interfaces, these implement them over Catalog's
// repositories/commands and pure UnitConverter, so the Recipes projects stay → SharedKernel only.
builder.Services.AddScoped<ICatalogProductReader, CatalogProductReaderAdapter>();
builder.Services.AddScoped<ICatalogWriter, CatalogWriterAdapter>();
builder.Services.AddScoped<IUnitConverter, RecipesUnitConverterAdapter>();

// Recipes → Inventory anti-corruption adapters (P2-2a / P2-3b, recipes-domain-model.md §8).
// Read port supplies FulfillmentService with live stock snapshots (available qty + soonest expiry).
// Write port (IInventoryConsumer) lets the Cook flow decrement the pantry via Inventory's single
// Consume primitive without the Recipes context touching Inventory tables directly (ADR-011).
builder.Services.AddScoped<IInventoryStockReader, InventoryStockReaderAdapter>();
builder.Services.AddScoped<IInventoryConsumer, InventoryConsumerAdapter>();

// Recipes → Pricing anti-corruption adapter (P2-2b, recipes-domain-model.md §8). Supplies
// CostingService with the latest PriceObservation per product from the Pricing context.
builder.Services.AddScoped<IPriceReader, PriceReaderAdapter>();

// Recipe domain services (P2-2a/P2-2b). Both are pure domain computations over their ports.
builder.Services.AddScoped<FulfillmentService>();
builder.Services.AddScoped<CostingService>();

// Recipe authoring application service (P2-1c, recipes-domain-model.md §7) — orchestrates create/edit
// over the Catalog ports + the recipe/tag repositories. Consumed by the P2-1d editor page.
builder.Services.AddScoped<AuthorRecipe>();

// Tag management application service (plantry-7ju). Drives the /Settings/Tags admin page:
// create/rename/set-category/archive/unarchive over the ITagRepository.
builder.Services.AddScoped<ManageTagsService>();

// Recipe browse query (P2-2c, J1/J2). Assembles the browse view model: lean recipe list + live
// fulfillment/cost per recipe + filter/sort in the application layer.
builder.Services.AddScoped<BrowseRecipesQuery>();

// Reconcile-pending-cooks service (P2-3d / plantry-292c). Re-drives Pending consume lines left by
// interrupted cooks — called opportunistically at CookRecipe entry and on-demand via the dedicated
// endpoint. No background poller (ADR-010 defers infra until needed).
builder.Services.AddScoped<ReconcilePendingCooks>();

// Cook-a-recipe application service (P2-3c, recipes-domain-model.md §7). Drives the J4 cook flow:
// ServingsScale + variant resolution (C7/C11) + atomic consume + cook event write (§7/§8).
// Runs an opportunistic reconciliation sweep (292c) at entry before starting the new cook.
builder.Services.AddScoped<CookRecipe>();

// Recipes → Shopping anti-corruption write adapter (P2-4a, recipes-domain-model.md §8 IShoppingListWriter).
// ShoppingListWriterAdapter implements the port over Shopping's AddItemCommand, stamping source=recipe +
// source_ref=recipeId and delegating the merge rule to Shopping (DM-18 / shopping.md resolved call 5).
builder.Services.AddScoped<IShoppingListWriter, ShoppingListWriterAdapter>();

// Add-missing-to-shopping-list application service (P2-4a, recipes-domain-model.md §7, J5).
// Computes a fresh FulfillmentResult at the displayed servings, takes Missing lines (excluding untracked),
// scales quantities, and calls IShoppingListWriter.AddItems(source=recipe, source_ref=recipeId).
builder.Services.AddScoped<AddMissingToShoppingList>();

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));

// Grocy import pipeline (plantry-zcw.1). GrocyClient (typed HttpClient) + ExtractCommand
// for the Extract stage. Config from "Grocy" section (user secrets in dev, env vars in prod).
builder.Services.AddGrocyImport(builder.Configuration);

// The real Gemini parser is the production default. Three deterministic alternatives replace it:
//  • AI:UseSampleParser=true → SampleReceiptParser, a real scanned receipt for local UI iteration (dev only);
//  • AI:UseFakeParser=true   → FakeReceiptParser, the fixed E2E journey fixture (set only by the E2E AppHost).
//  • no AI:ApiKey configured → DisabledReceiptParser, lets the app start with a locked-feature UI instead of crashing.
// Sample takes precedence over fake. Never enable either seam outside dev/test.
if (builder.Configuration.GetValue<bool>($"{AiOptions.SectionName}:UseSampleParser"))
    builder.Services.AddScoped<IReceiptParser, SampleReceiptParser>();
else if (builder.Configuration.GetValue<bool>($"{AiOptions.SectionName}:UseFakeParser"))
    builder.Services.AddScoped<IReceiptParser, FakeReceiptParser>();
else if (string.IsNullOrWhiteSpace(builder.Configuration[$"{AiOptions.SectionName}:ApiKey"]))
    builder.Services.AddScoped<IReceiptParser, DisabledReceiptParser>();
else
    builder.Services.AddScoped<IReceiptParser, GeminiReceiptParser>();
builder.Services.AddScoped<ICatalogHintProvider, CatalogHintProvider>();
builder.Services.AddScoped<ICreateProductPort, CreateProductAdapter>();
builder.Services.AddScoped<IAddStockPort, AddStockAdapter>();
builder.Services.AddScoped<IRecordPricePort, RecordPriceAdapter>();
builder.Services.AddScoped<IReviewReferenceDataProvider, ReviewReferenceDataProvider>();

if (builder.Environment.IsDevelopment())
    builder.Services.AddScoped<FakeDataSeeder>();

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
    // Dev-only endpoints for the Aspire dashboard seed commands.
    // Gated by DevPagesGateMiddleware above (returns 404 in non-Development).
    app.MapPost("/Dev/Seed", async (FakeDataSeeder seeder, CancellationToken ct) =>
    {
        await seeder.SeedAsync(ct);
        return Results.Ok();
    });

    app.MapPost("/Dev/Reset", async (FakeDataSeeder seeder, CancellationToken ct) =>
    {
        await seeder.ResetAndSeedAsync(ct);
        return Results.Ok();
    });
}

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapDefaultEndpoints();

app.Run();
