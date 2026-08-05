using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration tests for the <see cref="Substitution"/> table (plantry-aqpa.1). Covers: schema
/// round-trip via the repository, the UNIQUE (household_id, substitute_product_id, target_product_id)
/// constraint, the no-self / positive-quantity CHECK constraints, EF query filter household isolation,
/// the RLS backstop, and <c>SubstitutionReader</c>'s two read directions — mirrors
/// <c>RecipeRatingTests</c>. Product/unit ids are bare soft-refs (DM-3), so unlike RecipeRating there is
/// no composite FK to a parent table to test.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SubstitutionTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private static readonly IClock Clock = SystemClock.Instance;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Schema round-trip ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Substitution round-trips through the repository")]
    public async Task RoundTrip_Substitution()
    {
        var targetProduct = Guid.NewGuid();
        var targetUnit = Guid.NewGuid();
        var substituteProduct = Guid.NewGuid();
        var substituteUnit = Guid.NewGuid();

        await using (var writeDb = NewRecipesDb(_householdA))
        {
            var repo = new SubstitutionRepository(writeDb);
            var edge = Substitution.Create(
                _householdA, targetProduct, 400m, targetUnit, substituteProduct, 154m, substituteUnit, Clock);
            await repo.AddAsync(edge);
            await repo.SaveChangesAsync();
        }

        await using var readDb = NewRecipesDb(_householdA);
        var readRepo = new SubstitutionRepository(readDb);
        var loaded = await readRepo.FindByPairAsync(substituteProduct, targetProduct);

        Assert.NotNull(loaded);
        Assert.Equal(_householdA, loaded.HouseholdId);
        Assert.Equal(targetProduct, loaded.TargetProductId);
        Assert.Equal(400m, loaded.TargetQuantity);
        Assert.Equal(targetUnit, loaded.TargetUnitId);
        Assert.Equal(substituteProduct, loaded.SubstituteProductId);
        Assert.Equal(154m, loaded.SubstituteQuantity);
        Assert.Equal(substituteUnit, loaded.SubstituteUnitId);
    }

    [Fact(DisplayName = "Deleting an edge (repository Remove) removes the row")]
    public async Task Remove_Deletes_The_Row()
    {
        var targetProduct = Guid.NewGuid();
        var substituteProduct = Guid.NewGuid();

        await using (var writeDb = NewRecipesDb(_householdA))
        {
            var repo = new SubstitutionRepository(writeDb);
            var edge = Substitution.Create(
                _householdA, targetProduct, 400m, Guid.NewGuid(), substituteProduct, 154m, Guid.NewGuid(), Clock);
            await repo.AddAsync(edge);
            await repo.SaveChangesAsync();
        }

        await using (var deleteDb = NewRecipesDb(_householdA))
        {
            var repo = new SubstitutionRepository(deleteDb);
            var existing = await repo.FindByPairAsync(substituteProduct, targetProduct);
            Assert.NotNull(existing);
            repo.Remove(existing);
            await repo.SaveChangesAsync();
        }

        await using var readDb = NewRecipesDb(_householdA);
        var readRepo = new SubstitutionRepository(readDb);
        Assert.Null(await readRepo.FindByPairAsync(substituteProduct, targetProduct));
    }

    [Fact(DisplayName = "UNIQUE (household_id, substitute_product_id, target_product_id) — duplicate directed pair throws")]
    public async Task UniqueConstraint_ThrowsOnDuplicateDirectedPair()
    {
        var targetProduct = Guid.NewGuid();
        var substituteProduct = Guid.NewGuid();

        await using var writeDb = NewRecipesDb(_householdA);
        var repo = new SubstitutionRepository(writeDb);
        var edge = Substitution.Create(
            _householdA, targetProduct, 400m, Guid.NewGuid(), substituteProduct, 154m, Guid.NewGuid(), Clock);
        await repo.AddAsync(edge);
        await repo.SaveChangesAsync();

        // Insert a second row for the same directed pair via raw SQL, bypassing the domain's own
        // upsert discipline — the DB constraint must reject it regardless.
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO recipes.substitution
                (substitution_id, household_id, target_product_id, target_quantity, target_unit_id,
                 substitute_product_id, substitute_quantity, substitute_unit_id, created_at, updated_at)
            VALUES
                (gen_random_uuid(), '{_householdA.Value}', '{targetProduct}', 500, gen_random_uuid(),
                 '{substituteProduct}', 200, gen_random_uuid(), now(), now())
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23505", ex.SqlState); // unique_violation
    }

    [Fact(DisplayName = "The reverse directed pair (B->A) is a distinct row, not blocked by the unique constraint")]
    public async Task ReverseDirectedPair_IsAllowed()
    {
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();

        await using var db1 = NewRecipesDb(_householdA);
        var repo = new SubstitutionRepository(db1);
        await repo.AddAsync(Substitution.Create(
            _householdA, productB, 260m, Guid.NewGuid(), productA, 100m, Guid.NewGuid(), Clock)); // A -> B
        await repo.AddAsync(Substitution.Create(
            _householdA, productA, 100m, Guid.NewGuid(), productB, 260m, Guid.NewGuid(), Clock)); // B -> A
        await repo.SaveChangesAsync();

        var forward = await repo.FindByPairAsync(productA, productB);
        var reverse = await repo.FindByPairAsync(productB, productA);
        Assert.NotNull(forward);
        Assert.NotNull(reverse);
        Assert.NotEqual(forward.Id, reverse.Id);
    }

    [Fact(DisplayName = "CHECK constraint rejects self-substitution")]
    public async Task Check_RejectsSelfSubstitution()
    {
        var product = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO recipes.substitution
                (substitution_id, household_id, target_product_id, target_quantity, target_unit_id,
                 substitute_product_id, substitute_quantity, substitute_unit_id, created_at, updated_at)
            VALUES
                (gen_random_uuid(), '{_householdA.Value}', '{product}', 400, gen_random_uuid(),
                 '{product}', 154, gen_random_uuid(), now(), now())
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Theory(DisplayName = "CHECK constraints reject non-positive quantities")]
    [InlineData(0, 154)]
    [InlineData(400, 0)]
    [InlineData(-1, 154)]
    public async Task Check_RejectsNonPositiveQuantities(decimal targetQty, decimal substituteQty)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO recipes.substitution
                (substitution_id, household_id, target_product_id, target_quantity, target_unit_id,
                 substitute_product_id, substitute_quantity, substitute_unit_id, created_at, updated_at)
            VALUES
                (gen_random_uuid(), '{_householdA.Value}', '{Guid.NewGuid()}', {targetQty}, gen_random_uuid(),
                 '{Guid.NewGuid()}', {substituteQty}, gen_random_uuid(), now(), now())
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact(DisplayName = "CHECK constraint rejects an all-zeros (empty) unit id")]
    public async Task Check_RejectsEmptyUnitId()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO recipes.substitution
                (substitution_id, household_id, target_product_id, target_quantity, target_unit_id,
                 substitute_product_id, substitute_quantity, substitute_unit_id, created_at, updated_at)
            VALUES
                (gen_random_uuid(), '{_householdA.Value}', '{Guid.NewGuid()}', 400,
                 '00000000-0000-0000-0000-000000000000',
                 '{Guid.NewGuid()}', 154, gen_random_uuid(), now(), now())
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's edges")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Edges()
    {
        await using (var seedDb = NewRecipesDb(_householdB))
        {
            var repo = new SubstitutionRepository(seedDb);
            await repo.AddAsync(Substitution.Create(
                _householdB, Guid.NewGuid(), 400m, Guid.NewGuid(), Guid.NewGuid(), 154m, Guid.NewGuid(), Clock));
            await repo.SaveChangesAsync();
        }

        await using var readAsA = NewRecipesDb(_householdA);
        var found = await readAsA.Substitutions.ToListAsync();

        Assert.Empty(found);
    }

    [Fact(DisplayName = "RLS backstop: raw SQL with wrong household returns no substitution rows")]
    public async Task RlsPolicy_RawSql_WrongHousehold_ReturnsNoRows()
    {
        await using (var seedDb = NewRecipesDb(_householdB))
        {
            var repo = new SubstitutionRepository(seedDb);
            await repo.AddAsync(Substitution.Create(
                _householdB, Guid.NewGuid(), 400m, Guid.NewGuid(), Guid.NewGuid(), 154m, Guid.NewGuid(), Clock));
            await repo.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var select = conn.CreateCommand();
        select.CommandText = "SELECT substitution_id FROM recipes.substitution";
        await using var reader = await select.ExecuteReaderAsync();

        var ids = new List<Guid>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));

        Assert.Empty(ids);
    }

    [Fact(DisplayName = "RLS backstop (live path): interceptor arms app.household_id; only own household's edges visible")]
    public async Task Interceptor_OnAppUserConnection_RlsRestrictsEdgesToHousehold()
    {
        await using (var seedDb = NewRecipesDb(_householdA))
        {
            var repo = new SubstitutionRepository(seedDb);
            await repo.AddAsync(Substitution.Create(
                _householdA, Guid.NewGuid(), 400m, Guid.NewGuid(), Guid.NewGuid(), 154m, Guid.NewGuid(), Clock));
            await repo.SaveChangesAsync();
        }
        await using (var seedDb = NewRecipesDb(_householdB))
        {
            var repo = new SubstitutionRepository(seedDb);
            await repo.AddAsync(Substitution.Create(
                _householdB, Guid.NewGuid(), 400m, Guid.NewGuid(), Guid.NewGuid(), 154m, Guid.NewGuid(), Clock));
            await repo.SaveChangesAsync();
        }

        var tenant = new TenantContext();
        tenant.Set(_householdA.Value);

        var opts = BuildRecipesOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var recipesDb = new RecipesDbContext(opts);

        var edges = await recipesDb.Substitutions.IgnoreQueryFilters().ToListAsync();

        Assert.NotEmpty(edges);
        Assert.All(edges, e => Assert.Equal(_householdA, e.HouseholdId));
    }

    [Fact(DisplayName = "RLS backstop (live path): no tenant context => strict policy returns no substitution rows")]
    public async Task Interceptor_NoTenantContext_StrictPolicy_ReturnsNoRows()
    {
        await using (var seedDb = NewRecipesDb(_householdA))
        {
            var repo = new SubstitutionRepository(seedDb);
            await repo.AddAsync(Substitution.Create(
                _householdA, Guid.NewGuid(), 400m, Guid.NewGuid(), Guid.NewGuid(), 154m, Guid.NewGuid(), Clock));
            await repo.SaveChangesAsync();
        }

        var tenant = new TenantContext(); // never set

        var opts = BuildRecipesOptions(db.AppUserConnectionString, new HouseholdRlsConnectionInterceptor(tenant));
        await using var recipesDb = new RecipesDbContext(opts);

        var edges = await recipesDb.Substitutions.IgnoreQueryFilters().ToListAsync();

        Assert.Empty(edges);
    }

    // ── SubstitutionReader ────────────────────────────────────────────────────

    [Fact(DisplayName = "ListByTargetProductIdsAsync groups edges by target and returns only the requested ids")]
    public async Task ListByTargetProductIdsAsync_Returns_Edges_Grouped_By_Target()
    {
        var targetX = Guid.NewGuid();
        var targetY = Guid.NewGuid();
        var substituteA = Guid.NewGuid();
        var substituteB = Guid.NewGuid();

        await using (var seedDb = NewRecipesDb(_householdA))
        {
            var repo = new SubstitutionRepository(seedDb);
            await repo.AddAsync(Substitution.Create(
                _householdA, targetX, 400m, Guid.NewGuid(), substituteA, 154m, Guid.NewGuid(), Clock));
            await repo.AddAsync(Substitution.Create(
                _householdA, targetY, 100m, Guid.NewGuid(), substituteB, 50m, Guid.NewGuid(), Clock));
            await repo.SaveChangesAsync();
        }

        await using var readDb = NewRecipesDb(_householdA);
        var reader = new SubstitutionReader(readDb);

        var both = await reader.ListByTargetProductIdsAsync([targetX, targetY]);
        Assert.Equal(2, both.Count);
        Assert.Equal(substituteA, Assert.Single(both[targetX]).SubstituteProductId);
        Assert.Equal(substituteB, Assert.Single(both[targetY]).SubstituteProductId);

        var onlyX = await reader.ListByTargetProductIdsAsync([targetX]);
        Assert.Single(onlyX);
        Assert.False(onlyX.ContainsKey(targetY));
    }

    [Fact(DisplayName = "ListByTargetProductIdsAsync with an empty list returns an empty result")]
    public async Task ListByTargetProductIdsAsync_EmptyList_Returns_Empty()
    {
        await using var readDb = NewRecipesDb(_householdA);
        var reader = new SubstitutionReader(readDb);

        var result = await reader.ListByTargetProductIdsAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListTouchingProductAsync returns edges where the product is either target or substitute")]
    public async Task ListTouchingProductAsync_Returns_Edges_Either_Side()
    {
        var chickpeasDried = Guid.NewGuid();
        var chickpeasCanned = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        await using (var seedDb = NewRecipesDb(_householdA))
        {
            var repo = new SubstitutionRepository(seedDb);
            // dried substitutes for canned
            await repo.AddAsync(Substitution.Create(
                _householdA, chickpeasCanned, 260m, Guid.NewGuid(), chickpeasDried, 100m, Guid.NewGuid(), Clock));
            // unrelated pair not touching chickpeasDried
            await repo.AddAsync(Substitution.Create(
                _householdA, unrelated, 1m, Guid.NewGuid(), Guid.NewGuid(), 1m, Guid.NewGuid(), Clock));
            await repo.SaveChangesAsync();
        }

        await using var readDb = NewRecipesDb(_householdA);
        var reader = new SubstitutionReader(readDb);

        var touchingDried = await reader.ListTouchingProductAsync(chickpeasDried);
        var edge = Assert.Single(touchingDried);
        Assert.Equal(chickpeasCanned, edge.TargetProductId);
        Assert.Equal(chickpeasDried, edge.SubstituteProductId);

        var touchingCanned = await reader.ListTouchingProductAsync(chickpeasCanned);
        Assert.Single(touchingCanned);
    }

    [Fact(DisplayName = "SubstitutionReader: household A cannot see household B's edges via either method")]
    public async Task Reader_HouseholdA_Cannot_See_HouseholdB_Edges()
    {
        var targetB = Guid.NewGuid();
        var substituteB = Guid.NewGuid();

        await using (var seedDb = NewRecipesDb(_householdB))
        {
            var repo = new SubstitutionRepository(seedDb);
            await repo.AddAsync(Substitution.Create(
                _householdB, targetB, 400m, Guid.NewGuid(), substituteB, 154m, Guid.NewGuid(), Clock));
            await repo.SaveChangesAsync();
        }

        await using var readAsA = NewRecipesDb(_householdA);
        var reader = new SubstitutionReader(readAsA);

        Assert.Empty(await reader.ListByTargetProductIdsAsync([targetB]));
        Assert.Empty(await reader.ListTouchingProductAsync(targetB));
        Assert.Empty(await reader.ListTouchingProductAsync(substituteB));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private DbContextOptions<RecipesDbContext> RecipesOptions() =>
        new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(db.ConnectionString).Options;

    private static DbContextOptions<RecipesDbContext> BuildRecipesOptions(string connStr, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(connStr);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private RecipesDbContext NewRecipesDb(HouseholdId household)
    {
        var ctx = new RecipesDbContext(RecipesOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
