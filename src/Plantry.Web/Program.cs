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
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Dev;
using Plantry.Web.Events;
using Plantry.Web.Intake;
using Plantry.Web.Inventory;
using Plantry.Web.Pricing;
using Plantry.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorPages();

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

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
builder.Services.AddScoped<IReceiptParser, GeminiReceiptParser>();
builder.Services.AddScoped<ICatalogHintProvider, CatalogHintProvider>();
builder.Services.AddScoped<ICreateProductPort, CreateProductAdapter>();
builder.Services.AddScoped<IAddStockPort, AddStockAdapter>();
builder.Services.AddScoped<IRecordPricePort, RecordPriceAdapter>();

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
