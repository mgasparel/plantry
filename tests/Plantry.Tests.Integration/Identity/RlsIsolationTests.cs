using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Identity;

/// <summary>
/// L3 integration tests proving Postgres RLS enforces tenant isolation.
///
/// Key assertion (PHASE-1-PLAN.md §Testing strategy):
///   "A proof test that a query for household A physically cannot read household B's rows."
///
/// How it works:
///   1. Create two households (A and B) with reference data seeded.
///   2. Query units/categories/locations with household_A's ID set.
///   3. Assert household_B's rows are NOT returned.
///   4. Repeat for the Postgres RLS policy directly (bypass EF query filter).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RlsIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();

        var clock = SystemClock.Instance;
        var identityOpts = BuildIdentityOptions();
        var catalogOpts  = BuildCatalogOptions();

        // Create household A
        await using (var identityDb = new PlantryIdentityDbContext(identityOpts))
        await using (var catalogDb  = new CatalogDbContext(catalogOpts))
        {
            var householdA = Household.Create("Household A", clock);
            _householdA = householdA.Id;
            await identityDb.Households.AddAsync(householdA);
            await identityDb.SaveChangesAsync();

            var seeder = new CatalogReferenceDataSeeder(catalogDb);
            await seeder.SeedAsync(_householdA);
        }

        // Create household B
        await using (var identityDb = new PlantryIdentityDbContext(identityOpts))
        await using (var catalogDb  = new CatalogDbContext(catalogOpts))
        {
            var householdB = Household.Create("Household B", clock);
            _householdB = householdB.Id;
            await identityDb.Households.AddAsync(householdB);
            await identityDb.SaveChangesAsync();

            var seeder = new CatalogReferenceDataSeeder(catalogDb);
            await seeder.SeedAsync(_householdB);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "EF query filter: household A cannot see household B's units")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Units()
    {
        var opts = BuildCatalogOptions();
        await using var catalogDb = new CatalogDbContext(opts);

        // Activate as household A
        catalogDb.SetHouseholdId(_householdA.Value);

        var units = await catalogDb.Units.ToListAsync();

        Assert.All(units, u => Assert.Equal(_householdA.Value, u.HouseholdId));
        Assert.DoesNotContain(units, u => u.HouseholdId == _householdB.Value);
    }

    [Fact(DisplayName = "EF query filter: household B cannot see household A's categories")]
    public async Task EfFilter_HouseholdB_Cannot_Read_HouseholdA_Categories()
    {
        var opts = BuildCatalogOptions();
        await using var catalogDb = new CatalogDbContext(opts);

        catalogDb.SetHouseholdId(_householdB.Value);

        var categories = await catalogDb.Categories.ToListAsync();

        Assert.All(categories, c => Assert.Equal(_householdB.Value, c.HouseholdId));
        Assert.DoesNotContain(categories, c => c.HouseholdId == _householdA.Value);
    }

    [Fact(DisplayName = "Postgres RLS backstop: raw SQL with wrong app.household_id returns no rows")]
    public async Task RlsPolicy_RawSql_WithWrongHouseholdId_ReturnsNoRows()
    {
        // Bypass EF entirely — raw connection to verify the Postgres RLS policy fires.
        // Must connect as the non-superuser 'app_user' role: RLS never applies to
        // superusers (the Testcontainers bootstrap user is one), so a superuser
        // connection would silently bypass every policy, including FORCE ROW LEVEL SECURITY.
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        // Set app.household_id to household A's ID on the connection
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        // Query catalog.locations — should only return household A's rows
        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT household_id FROM catalog.locations";
        await using var reader = await selectCmd.ExecuteReaderAsync();

        var seenIds = new List<Guid>();
        while (await reader.ReadAsync())
            seenIds.Add(reader.GetGuid(0));

        Assert.NotEmpty(seenIds);
        Assert.All(seenIds, id => Assert.Equal(_householdA.Value, id));
        Assert.DoesNotContain(seenIds, id => id == _householdB.Value);
    }

    [Fact(DisplayName = "Both households have distinct, non-overlapping unit sets")]
    public async Task EachHousehold_Has_Independent_UnitSet()
    {
        var opts = BuildCatalogOptions();

        await using var dbA = new CatalogDbContext(opts);
        dbA.SetHouseholdId(_householdA.Value);
        var unitsA = await dbA.Units.Select(u => u.Id).ToListAsync();

        await using var dbB = new CatalogDbContext(opts);
        dbB.SetHouseholdId(_householdB.Value);
        var unitsB = await dbB.Units.Select(u => u.Id).ToListAsync();

        Assert.NotEmpty(unitsA);
        Assert.NotEmpty(unitsB);
        Assert.Empty(unitsA.Intersect(unitsB)); // Completely disjoint IDs
    }

    // ── The running-app path: RLS armed via the connection interceptor ─────────
    // The tests above prove the EF filter and the raw policy in isolation. These prove the
    // mechanism the *running app* actually uses: connect as the non-superuser app_user role and
    // let HouseholdRlsConnectionInterceptor SET app.household_id on the live connection. Each
    // query below uses IgnoreQueryFilters(), so the EF app-layer filter is removed and anything
    // returned is proof the Postgres RLS policy alone is enforcing isolation.

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsToHousehold()
    {
        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildCatalogOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var catalogDb = new CatalogDbContext(opts);

        var locations = await catalogDb.Locations.IgnoreQueryFilters().ToListAsync();

        Assert.NotEmpty(locations);
        Assert.All(locations, l => Assert.Equal(_householdA.Value, l.HouseholdId));
        Assert.DoesNotContain(locations, l => l.HouseholdId == _householdB.Value);
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict catalog policy returns no rows")]
    public async Task Interceptor_NoTenantContext_StrictCatalogPolicy_ReturnsNoRows()
    {
        var tenant = new TenantContext(); // never set

        var opts = BuildCatalogOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var catalogDb = new CatalogDbContext(opts);

        var locations = await catalogDb.Locations.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(locations);
    }

    [Fact(DisplayName = "Identity RLS: no-context carve-out permits pre-auth lookups; tenant context isolates")]
    public async Task IdentityUsers_CarveOut_PermitsAuthLookups_AndIsolatesWhenScoped()
    {
        // Seed one user per household via the owner connection (superuser bypasses RLS).
        await using (var ownerDb = new PlantryIdentityDbContext(BuildIdentityOptions()))
        {
            ownerDb.Users.Add(NewUser("a@plantry.test", _householdA.Value));
            ownerDb.Users.Add(NewUser("b@plantry.test", _householdB.Value));
            await ownerDb.SaveChangesAsync();
        }

        // No tenant context — the login / cookie-revalidation path. The carve-out makes every
        // user visible so the framework can find a user before its household is known.
        var noTenant = new TenantContext();
        await using (var authDb = new PlantryIdentityDbContext(
            BuildIdentityOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(noTenant))))
        {
            Assert.Equal(2, await authDb.Users.CountAsync());
        }

        // Household A active — only A's user is visible, proving isolation once scoped.
        var tenantA = new TenantContext();
        tenantA.Set(_householdA.Value);
        await using (var scopedDb = new PlantryIdentityDbContext(
            BuildIdentityOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenantA))))
        {
            var scoped = await scopedDb.Users.ToListAsync();
            Assert.Single(scoped);
            Assert.All(scoped, u => Assert.Equal(_householdA.Value, u.HouseholdId));
        }
    }

    [Fact(DisplayName = "EF query filter: Identity context scoped to household A cannot see household B's household row")]
    public async Task EfFilter_IdentityContext_HouseholdA_Cannot_Read_HouseholdB_Household()
    {
        // Owner connection (RLS bypassed) so this isolates the app-layer EF query filter alone.
        await using var identityDb = new PlantryIdentityDbContext(BuildIdentityOptions());
        identityDb.SetHouseholdId(_householdA.Value);

        var households = await identityDb.Households.ToListAsync();

        Assert.Single(households);
        Assert.Equal(_householdA, households[0].Id);
        Assert.DoesNotContain(households, h => h.Id == _householdB);
    }

    [Fact(DisplayName = "Identity RLS: households table — no-context carve-out permits the registration insert path; tenant context isolates")]
    public async Task IdentityHouseholds_CarveOut_PermitsContextlessRead_AndIsolatesWhenScoped()
    {
        // Each query below uses IgnoreQueryFilters(), so the EF app-layer filter is removed and
        // anything returned is proof the Postgres RLS policy alone is enforcing isolation.

        // No tenant context — the path RegisterHouseholdCommand inserts the household row under.
        // The carve-out keeps every row visible so that contextless insert/read works.
        var noTenant = new TenantContext();
        await using (var authDb = new PlantryIdentityDbContext(
            BuildIdentityOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(noTenant))))
        {
            Assert.Equal(2, await authDb.Households.IgnoreQueryFilters().CountAsync());
        }

        // Household A active — RLS alone returns only A's own row, proving isolation once scoped.
        var tenantA = new TenantContext();
        tenantA.Set(_householdA.Value);
        await using (var scopedDb = new PlantryIdentityDbContext(
            BuildIdentityOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenantA))))
        {
            var scoped = await scopedDb.Households.IgnoreQueryFilters().ToListAsync();
            Assert.Single(scoped);
            Assert.Equal(_householdA, scoped[0].Id);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AppUser NewUser(string email, Guid householdId) => new()
    {
        Id = Guid.CreateVersion7().ToString(),
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        SecurityStamp = Guid.NewGuid().ToString(),
        ConcurrencyStamp = Guid.NewGuid().ToString(),
        HouseholdId = householdId,
        DisplayName = "Test",
    };

    private DbContextOptions<PlantryIdentityDbContext> BuildIdentityOptions() =>
        BuildIdentityOptions(db.ConnectionString);

    private static DbContextOptions<PlantryIdentityDbContext> BuildIdentityOptions(
        string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<PlantryIdentityDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private DbContextOptions<CatalogDbContext> BuildCatalogOptions() =>
        BuildCatalogOptions(db.ConnectionString);

    private static DbContextOptions<CatalogDbContext> BuildCatalogOptions(
        string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }
}
