using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
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
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRls();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapDefaultEndpoints();

app.Run();
