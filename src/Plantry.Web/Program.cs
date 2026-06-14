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
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Dev;
using Plantry.Web.Events;
using Plantry.Web.Intake;
using Plantry.Web.Inventory;
using Plantry.Web.Pricing;
using Plantry.Web.Recipes;
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

builder.Services.AddDbContext<IntakeDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Intake.Infrastructure"))
        .AddInterceptors(
            sp.GetRequiredService<HouseholdRlsConnectionInterceptor>(),
            sp.GetRequiredService<DomainEventDispatchInterceptor>()));
builder.Services.AddScoped<IImportSessionRepository, ImportSessionRepository>();

// Recipes context (Phase 2). P2-1 adds domain behaviour, EF child-collection mapping, and the
// IRecipeRepository; later P2 steps add application services.
builder.Services.AddDbContext<RecipesDbContext>((sp, opts) =>
    opts.UseNpgsql(appUserConnStr,
            npgsql => npgsql.MigrationsAssembly("Plantry.Recipes.Infrastructure"))
        .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
builder.Services.AddScoped<IRecipeRepository, RecipeRepository>();
builder.Services.AddScoped<ITagRepository, TagRepository>();
builder.Services.AddScoped<IReferenceDataSeeder, RecipesReferenceDataSeeder>();

// Recipes → Catalog anti-corruption adapters (P2-1b, recipes-domain-model.md §8). The Port +
// Web-adapter seam: Recipes.Application owns the interfaces, these implement them over Catalog's
// repositories/commands and pure UnitConverter, so the Recipes projects stay → SharedKernel only.
builder.Services.AddScoped<ICatalogProductReader, CatalogProductReaderAdapter>();
builder.Services.AddScoped<ICatalogWriter, CatalogWriterAdapter>();
builder.Services.AddScoped<IUnitConverter, RecipesUnitConverterAdapter>();

// Recipes → Inventory anti-corruption adapter (P2-2a, recipes-domain-model.md §8). Supplies
// FulfillmentService with live stock snapshots (available qty + soonest expiry) from Inventory.
builder.Services.AddScoped<IInventoryStockReader, InventoryStockReaderAdapter>();

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
