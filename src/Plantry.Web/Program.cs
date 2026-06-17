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
builder.Services.AddRazorPages(options =>
{
    // Explicit create-recipe route: the page-level route "/Recipes/{id:guid?}/Edit" only matches when
    // an id is present (ASP.NET Core does not collapse optional mid-path segments into a shorter URL).
    // Adding "/Recipes/New" as an alias routes the new-recipe form without an id binding, which causes
    // EditModel.Id to be null → create branch (J6).
    options.Conventions.AddPageRoute("/Recipes/Edit", "Recipes/New");
});

// The injected connection string is the database owner — used for migrations (which create
// roles, schemas, and RLS policies). At runtime the app instead connects as the non-superuser
// 'app_user' role so the Postgres RLS policies actually apply (RLS, even FORCE, never applies
// to superusers/owners). See the InitialCatalogSchema / InitialIdentitySchema migrations.
var ownerConnStr = builder.Configuration.GetConnectionString("plantrydb")
    ?? "Host=localhost;Database=plantrydb;Username=postgres;Password=postgres";

var appUserConnStr = new NpgsqlConnectionStringBuilder(ownerConnStr)
{
    Username = "app_user",
    Password = "app_user_password",
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

if (app.Environment.IsDevelopment())
{
    // Migrations run as the database owner (creating the app_user role, schemas, and RLS
    // policies), NOT as the runtime app_user role — so build throwaway owner-connection
    // contexts here rather than resolving the app_user-scoped DI contexts.
    var identityMigrateOpts = new DbContextOptionsBuilder<PlantryIdentityDbContext>()
        .UseNpgsql(ownerConnStr, npgsql => npgsql.MigrationsAssembly("Plantry.Identity.Infrastructure"))
        .Options;
    await using (var identityDb = new PlantryIdentityDbContext(identityMigrateOpts))
        await identityDb.Database.MigrateAsync();

    var catalogMigrateOpts = new DbContextOptionsBuilder<CatalogDbContext>()
        .UseNpgsql(ownerConnStr, npgsql => npgsql.MigrationsAssembly("Plantry.Catalog.Infrastructure"))
        .Options;
    await using (var catalogDb = new CatalogDbContext(catalogMigrateOpts))
        await catalogDb.Database.MigrateAsync();

    var inventoryMigrateOpts = new DbContextOptionsBuilder<InventoryDbContext>()
        .UseNpgsql(ownerConnStr, npgsql => npgsql.MigrationsAssembly("Plantry.Inventory.Infrastructure"))
        .Options;
    await using (var inventoryDb = new InventoryDbContext(inventoryMigrateOpts))
        await inventoryDb.Database.MigrateAsync();

    var pricingMigrateOpts = new DbContextOptionsBuilder<PricingDbContext>()
        .UseNpgsql(ownerConnStr, npgsql => npgsql.MigrationsAssembly("Plantry.Pricing.Infrastructure"))
        .Options;
    await using (var pricingDb = new PricingDbContext(pricingMigrateOpts))
        await pricingDb.Database.MigrateAsync();

    var intakeMigrateOpts = new DbContextOptionsBuilder<IntakeDbContext>()
        .UseNpgsql(ownerConnStr, npgsql => npgsql.MigrationsAssembly("Plantry.Intake.Infrastructure"))
        .Options;
    await using (var intakeDb = new IntakeDbContext(intakeMigrateOpts))
        await intakeDb.Database.MigrateAsync();

    var recipesMigrateOpts = new DbContextOptionsBuilder<RecipesDbContext>()
        .UseNpgsql(ownerConnStr, npgsql => npgsql.MigrationsAssembly("Plantry.Recipes.Infrastructure"))
        .Options;
    await using (var recipesDb = new RecipesDbContext(recipesMigrateOpts))
        await recipesDb.Database.MigrateAsync();

    var shoppingMigrateOpts = new DbContextOptionsBuilder<ShoppingDbContext>()
        .UseNpgsql(ownerConnStr, npgsql => npgsql.MigrationsAssembly("Plantry.Shopping.Infrastructure"))
        .Options;
    await using (var shoppingDb = new ShoppingDbContext(shoppingMigrateOpts))
        await shoppingDb.Database.MigrateAsync();

    var mealPlanningMigrateOpts = new DbContextOptionsBuilder<MealPlanningDbContext>()
        .UseNpgsql(ownerConnStr, npgsql => npgsql.MigrationsAssembly("Plantry.MealPlanning.Infrastructure"))
        .Options;
    await using (var mealPlanningDb = new MealPlanningDbContext(mealPlanningMigrateOpts))
        await mealPlanningDb.Database.MigrateAsync();

    // Auto-seed on first startup: no-ops if the demo user already exists.
    await using var seedScope = app.Services.CreateAsyncScope();
    await seedScope.ServiceProvider.GetRequiredService<FakeDataSeeder>().SeedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseDevPagesGate();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
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
