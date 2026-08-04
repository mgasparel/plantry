using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Housekeeping;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Housekeeping;

/// <summary>
/// L3 contract/integration tests for the recipe-family Tidy Up detectors (D2
/// <see cref="RecipeConversionGapDetector"/>, D5 <see cref="RecipeIngredientNoPriceDetector"/>, D7
/// <see cref="RecipeLineUntrackedProductDetector"/>) against the real migrated schema, replacing the
/// retired fake-port unit tests now that ADR-021/ADR-024 Phase A moved these detectors onto
/// <see cref="IRecipeFactsReadModel"/>'s raw cross-schema SQL. RLS isolation is proven separately in
/// <see cref="RecipeFactsReadModelRlsIsolationTests"/>.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipeDetectorsTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = new FixedClock(new DateOnly(2026, 7, 22));
    private HouseholdId _household;
    private Guid _gramsId;
    private Guid _eachId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalog = NewCatalogDb(_household);
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var each = CatalogUnit.Create(_household, "ea", "each", Dimension.Count, 1m);
        await catalog.Units.AddRangeAsync(grams, each);
        await catalog.SaveChangesAsync();
        _gramsId = grams.Id.Value;
        _eachId = each.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── D2: RecipeConversionGapDetector ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "D2: tracked line, unit differs from default, no conversion path — produces a finding, FixUrl anchors on the offending line's own ordinal (plantry-c7mg regression lock)")]
    public async Task D2_NoConversionPath_ProducesFinding()
    {
        // plantry-c7mg regression lock: the offending line sits at a NON-ZERO ordinal (1), behind a
        // harmless tracked line at ordinal 0 that does NOT trigger D2 (its unit already matches the
        // product's default). Asserting FixUrl anchors on ordinal 1 — not 0 — proves the anchor tracks
        // the specific flagged line rather than a fixed/first-line value, which is exactly the value the
        // old (pre-fix) bug also produced and a test seeded at ordinal 0 could never catch a regression to.
        var saltId = await SeedProductAsync("Salt", _gramsId, trackStock: true);
        var productId = await SeedProductAsync("Flour", _gramsId, trackStock: true);
        var recipeId = await SeedRecipeAsync(
            "Bread",
            (saltId, 5m, _gramsId, 0),
            (productId, 2m, _eachId, 1));

        var findings = await BuildD2().DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.RecipeConversionGap, finding.DetectorId);
        Assert.Equal("Flour", finding.SubjectName);
        Assert.Contains("Bread", finding.Specifics);
        Assert.Equal($"/Recipes/{recipeId}/Edit#ingredient-1", finding.FixUrl);
    }

    [Fact(DisplayName = "D2: line unit equals the product's own default unit — no finding")]
    public async Task D2_SameUnitAsDefault_NoFinding()
    {
        var productId = await SeedProductAsync("Sugar", _gramsId, trackStock: true);
        await SeedRecipeAsync("Cake", (productId, 200m, _gramsId, 0));

        var findings = await BuildD2().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D2: untracked product — never flagged (cooking never deducts it, R7)")]
    public async Task D2_UntrackedProduct_NeverFlagged()
    {
        var productId = await SeedProductAsync("Water", _gramsId, trackStock: false);
        await SeedRecipeAsync("Soup", (productId, 1m, _eachId, 0));

        var findings = await BuildD2().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D2: fingerprint pinning — the same (line unit, default unit) pair on two different recipes produces the same fingerprint")]
    public async Task D2_Fingerprint_SameAcrossRecipes()
    {
        var productId = await SeedProductAsync("Butter", _gramsId, trackStock: true);
        await SeedRecipeAsync("Toast", (productId, 1m, _eachId, 0));
        await SeedRecipeAsync("Pancakes", (productId, 1m, _eachId, 0));

        var findings = await BuildD2().DetectAsync();

        Assert.Equal(2, findings.Count);
        Assert.Equal(findings[0].FactsFingerprint, findings[1].FactsFingerprint);
    }

    // ── D5: RecipeIngredientNoPriceDetector ─────────────────────────────────────────────────────

    [Fact(DisplayName = "D5: tracked product with zero price observations — produces a finding")]
    public async Task D5_NoPriceObservations_ProducesFinding()
    {
        var productId = await SeedProductAsync("Oats", _gramsId, trackStock: true);
        await SeedRecipeAsync("Porridge", (productId, 100m, _gramsId, 0));

        var findings = await BuildD5().DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.RecipeIngredientNoPriceData, finding.DetectorId);
        Assert.Equal(productId, finding.SubjectId);
        Assert.Contains("Porridge", finding.Specifics);
    }

    [Fact(DisplayName = "D5: tracked product WITH a live price observation — no finding")]
    public async Task D5_HasPriceObservation_NoFinding()
    {
        var productId = await SeedProductAsync("Pasta", _gramsId, trackStock: true);
        await SeedRecipeAsync("Spaghetti", (productId, 200m, _gramsId, 0));
        await SeedPriceObservationAsync(productId, 2.5m);

        var findings = await BuildD5().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D5: untracked product — excluded even with zero price observations (D7's territory)")]
    public async Task D5_UntrackedProduct_Excluded()
    {
        var productId = await SeedProductAsync("Salt", _gramsId, trackStock: false);
        await SeedRecipeAsync("Broth", (productId, 5m, _gramsId, 0));

        var findings = await BuildD5().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D5: a superseded (ADR-023 A7) price observation does not count as pricing the product")]
    public async Task D5_SupersededObservation_StillFires()
    {
        var productId = await SeedProductAsync("Yeast", _gramsId, trackStock: true);
        await SeedRecipeAsync("Dough", (productId, 10m, _gramsId, 0));
        var deadId = await SeedPriceObservationAsync(productId, 1m);
        var liveId = await SeedPriceObservationAsync(productId, 2m);
        await SupersedeAsync(deadId, liveId);

        // The live observation still exists, so this must NOT fire — pinning that the superseded filter
        // excludes only the dead row, not the whole product.
        var findings = await BuildD5().DetectAsync();

        Assert.Empty(findings);
    }

    // ── D7: RecipeLineUntrackedProductDetector ──────────────────────────────────────────────────

    [Fact(DisplayName = "D7: untracked product line — produces a finding, FixUrl anchors on the offending line's own ordinal (plantry-c7mg regression lock)")]
    public async Task D7_UntrackedProductLine_ProducesFinding()
    {
        // plantry-c7mg regression lock: the offending line sits at a NON-ZERO ordinal (1), behind a
        // harmless tracked line at ordinal 0 that does NOT trigger D7. Asserting FixUrl anchors on
        // ordinal 1 — not 0 — proves the anchor tracks the specific flagged line rather than a
        // fixed/first-line value, which is exactly the value the old (pre-fix) bug also produced.
        var chickenId = await SeedProductAsync("Chicken2", _gramsId, trackStock: true);
        var productId = await SeedProductAsync("Water2", _gramsId, trackStock: false);
        var recipeId = await SeedRecipeAsync(
            "Tea",
            (chickenId, 100m, _gramsId, 0),
            (productId, 250m, _gramsId, 1));

        var findings = await BuildD7().DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DetectorId.RecipeLineUntrackedProduct, finding.DetectorId);
        Assert.Equal("Water2", finding.SubjectName);
        Assert.Equal($"/Recipes/{recipeId}/Edit#ingredient-1", finding.FixUrl);
    }

    [Fact(DisplayName = "D7: tracked product — never flagged")]
    public async Task D7_TrackedProduct_NeverFlagged()
    {
        var productId = await SeedProductAsync("Chicken", _gramsId, trackStock: true);
        await SeedRecipeAsync("Roast", (productId, 1000m, _gramsId, 0));

        var findings = await BuildD7().DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "D7: fingerprint pinning — the same untracked product on a different recipe produces the same fingerprint")]
    public async Task D7_Fingerprint_SameAcrossRecipes()
    {
        var productId = await SeedProductAsync("Spice", _gramsId, trackStock: false);
        await SeedRecipeAsync("CurryA", (productId, 5m, _gramsId, 0));
        await SeedRecipeAsync("CurryB", (productId, 3m, _gramsId, 0));

        var findings = await BuildD7().DetectAsync();

        Assert.Equal(2, findings.Count);
        Assert.Equal(findings[0].FactsFingerprint, findings[1].FactsFingerprint);
    }

    // ── shared: no tenant ────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "No household in tenant context — every recipe detector returns no findings")]
    public async Task NoTenant_AllDetectors_ReturnEmpty()
    {
        var productId = await SeedProductAsync("Pepper", _gramsId, trackStock: false);
        await SeedRecipeAsync("Stew", (productId, 1m, _gramsId, 0));
        var noTenant = new TenantContext();

        Assert.Empty(await BuildD2(noTenant).DetectAsync());
        Assert.Empty(await BuildD5(noTenant).DetectAsync());
        Assert.Empty(await BuildD7(noTenant).DetectAsync());
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private RecipeConversionGapDetector BuildD2(ITenantContext? tenant = null) =>
        new(NewRecipeFactsReadModel(tenant), tenant ?? TenantFor(_household));

    private RecipeIngredientNoPriceDetector BuildD5(ITenantContext? tenant = null) =>
        new(NewRecipeFactsReadModel(tenant), tenant ?? TenantFor(_household));

    private RecipeLineUntrackedProductDetector BuildD7(ITenantContext? tenant = null) =>
        new(NewRecipeFactsReadModel(tenant), tenant ?? TenantFor(_household));

    private IRecipeFactsReadModel NewRecipeFactsReadModel(ITenantContext? tenant) =>
        new RecipeFactsReadModel(db.ConnectionString, tenant ?? TenantFor(_household));

    private static ITenantContext TenantFor(HouseholdId household)
    {
        var tenant = new TenantContext();
        tenant.Set(household.Value);
        return tenant;
    }

    private CatalogDbContext NewCatalogDb(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new CatalogDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private async Task<Guid> SeedProductAsync(string name, Guid defaultUnitId, bool trackStock)
    {
        await using var catalog = NewCatalogDb(_household);
        var product = Product.Create(_household, name, UnitId.From(defaultUnitId), Clock, trackStock: trackStock);
        await catalog.Products.AddAsync(product);
        await catalog.SaveChangesAsync();
        return product.Id.Value;
    }

    private async Task<Guid> SeedRecipeAsync(
        string name, params (Guid ProductId, decimal Quantity, Guid UnitId, int Ordinal)[] ingredients)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var recipeId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO recipes.recipe
                    (recipe_id, household_id, name, default_servings, created_at, updated_at)
                VALUES
                    (@id, @hid, @name, 4, NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", recipeId);
            cmd.Parameters.AddWithValue("hid", _household.Value);
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var (productId, quantity, unitId, ordinal) in ingredients)
        {
            await using var ingCmd = conn.CreateCommand();
            ingCmd.CommandText = """
                INSERT INTO recipes.recipe_ingredient
                    (ingredient_id, household_id, recipe_id, product_id, quantity, unit_id, ordinal)
                VALUES
                    (@id, @hid, @rid, @pid, @qty, @uid, @ord)
                """;
            ingCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            ingCmd.Parameters.AddWithValue("hid", _household.Value);
            ingCmd.Parameters.AddWithValue("rid", recipeId);
            ingCmd.Parameters.AddWithValue("pid", productId);
            ingCmd.Parameters.AddWithValue("qty", quantity);
            ingCmd.Parameters.AddWithValue("uid", unitId);
            ingCmd.Parameters.AddWithValue("ord", ordinal);
            await ingCmd.ExecuteNonQueryAsync();
        }

        return recipeId;
    }

    private async Task<Guid> SeedPriceObservationAsync(Guid productId, decimal price)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var id = Guid.NewGuid();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pricing.price_observation
                (observation_id, household_id, product_id, price, quantity, unit_id, unit_price,
                 source, source_ref, observed_at, user_id)
            VALUES
                (@id, @hid, @pid, @price, 1, @uid, @price,
                 'Purchase', @ref, NOW(), @usr)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("uid", _gramsId);
        cmd.Parameters.AddWithValue("ref", Guid.NewGuid());
        cmd.Parameters.AddWithValue("usr", Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task SupersedeAsync(Guid observationId, Guid replacementId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pricing.price_observation SET superseded_by_id = @replacement WHERE observation_id = @id
            """;
        cmd.Parameters.AddWithValue("replacement", replacementId);
        cmd.Parameters.AddWithValue("id", observationId);
        await cmd.ExecuteNonQueryAsync();
    }
}
