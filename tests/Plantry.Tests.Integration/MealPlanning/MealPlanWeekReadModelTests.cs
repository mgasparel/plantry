using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.Pricing.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.MealPlanning;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests for <see cref="MealPlanWeekReadModel"/> (ADR-021, plantry-nz3u.1).
///
/// Proves that the cross-schema read model:
/// <list type="bullet">
///   <item>Executes its queries against the real migrated schema without error (contract: column names are stable).</item>
///   <item>Returns the same recipe, ingredient, product, stock, price, unit and conversion data
///     that the individual context ports would have returned.</item>
///   <item>Handles edge cases: no recipes, no stock, no prices.</item>
/// </list>
///
/// RLS isolation (two-household leakage test) belongs to plantry-nz3u.4 — not this suite.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanWeekReadModelTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;

    // Shared unit ids for the test household
    private Guid _gramsId;
    private Guid _kgId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        // Seed two units (grams + kilograms) used across all tests.
        await using var catalog = NewCatalogDb(_household);
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var kg = CatalogUnit.Create(_household, "kg", "kilograms", Dimension.Mass, 1000m);
        await catalog.Units.AddRangeAsync(grams, kg);
        await catalog.SaveChangesAsync();

        _gramsId = grams.Id.Value;
        _kgId = kg.Id.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── contract: schema column names are stable ─────────────────────────────────────────────────

    [Fact(DisplayName = "Contract: LoadAsync executes without error on a migrated schema (no data)")]
    public async Task Contract_LoadAsync_ExecutesOnMigratedSchema_WhenNoData()
    {
        var rm = NewReadModel(_household);

        // Empty week — no recipe ids, no product ids.
        // This test fails in CI when any column referenced in the SQL is renamed/dropped.
        var bag = await rm.LoadAsync([], []);

        Assert.NotNull(bag);
        Assert.Empty(bag.Recipes);
        Assert.Empty(bag.IngredientsByRecipe);
        Assert.Empty(bag.Products);
        Assert.Empty(bag.ConversionsByProduct);
        Assert.Empty(bag.StockByProduct);
        Assert.Empty(bag.LatestPriceByProduct);
        // Units are loaded regardless (all household units) — units table was seeded.
        Assert.NotEmpty(bag.Units);
    }

    // ── recipe + ingredient loading ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync returns recipe name and default servings from recipes schema")]
    public async Task LoadAsync_Returns_RecipeFact_From_RecipesSchema()
    {
        // Seed a recipe with two ingredients.
        var productId = await SeedProductAsync("Flour", _gramsId);
        var productId2 = await SeedProductAsync("Sugar", _gramsId);
        var recipeId = await SeedRecipeAsync("Cake", 4,
            (productId, 200m, _gramsId, 1),
            (productId2, 100m, _gramsId, 2));

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([recipeId], []);

        Assert.True(bag.Recipes.ContainsKey(recipeId));
        var recipe = bag.Recipes[recipeId];
        Assert.Equal("Cake", recipe.Name);
        Assert.Equal(4, recipe.DefaultServings);
    }

    [Fact(DisplayName = "LoadAsync returns ingredients for a recipe in ordinal order")]
    public async Task LoadAsync_Returns_Ingredients_InOrdinalOrder()
    {
        var productId1 = await SeedProductAsync("Flour2", _gramsId);
        var productId2 = await SeedProductAsync("Butter", _kgId);
        var recipeId = await SeedRecipeAsync("Bread", 2,
            (productId1, 300m, _gramsId, 1),
            (productId2, 0.1m, _kgId, 2));

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([recipeId], []);

        var ingredients = bag.GetIngredients(recipeId);
        Assert.Equal(2, ingredients.Count);
        Assert.Equal(1, ingredients[0].Ordinal);
        Assert.Equal(productId1, ingredients[0].ProductId);
        Assert.Equal(300m, ingredients[0].Quantity);
        Assert.Equal(_gramsId, ingredients[0].UnitId);
        Assert.Equal(2, ingredients[1].Ordinal);
        Assert.Equal(productId2, ingredients[1].ProductId);
    }

    [Fact(DisplayName = "LoadAsync returns product facts from catalog schema")]
    public async Task LoadAsync_Returns_ProductFacts_From_CatalogSchema()
    {
        var productId = await SeedProductAsync("Rice", _gramsId, trackStock: true);
        var recipeId = await SeedRecipeAsync("Rice dish", 2, (productId, 200m, _gramsId, 1));

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([recipeId], []);

        Assert.True(bag.Products.ContainsKey(productId));
        var product = bag.Products[productId];
        Assert.Equal("Rice", product.Name);
        Assert.True(product.TrackStock);
        Assert.Equal(_gramsId, product.DefaultUnitId);
    }

    // ── stock loading ────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync returns aggregated stock for tracked products")]
    public async Task LoadAsync_Returns_AggregatedStock_ForTrackedProducts()
    {
        var productId = await SeedProductAsync("Pasta", _gramsId, trackStock: true);
        var locationId = await SeedLocationAsync("Pantry");
        // Seed two active stock lots.
        await SeedStockEntryAsync(productId, locationId, 500m, _gramsId, expiryDate: null);
        await SeedStockEntryAsync(productId, locationId, 300m, _gramsId, expiryDate: new DateOnly(2026, 12, 31));

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        Assert.True(bag.StockByProduct.ContainsKey(productId));
        var stock = bag.StockByProduct[productId];
        Assert.True(stock.HasStock);
        // Both lots are in grams — total quantity should sum to 800g across the lots.
        var gramsLot = stock.Lots.FirstOrDefault(l => l.UnitId == _gramsId);
        Assert.NotNull(gramsLot);
        Assert.Equal(800m, gramsLot.TotalQuantity);
    }

    [Fact(DisplayName = "LoadAsync returns soonest expiry across stock lots")]
    public async Task LoadAsync_Returns_SoonestExpiry_AcrossLots()
    {
        var productId = await SeedProductAsync("Milk", _gramsId, trackStock: true);
        var locationId = await SeedLocationAsync("Fridge");
        var sooner = new DateOnly(2026, 7, 1);
        var later = new DateOnly(2026, 7, 10);
        await SeedStockEntryAsync(productId, locationId, 1000m, _gramsId, expiryDate: later);
        await SeedStockEntryAsync(productId, locationId, 500m, _gramsId, expiryDate: sooner);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var stock = bag.GetStock(productId);
        Assert.NotNull(stock);
        Assert.Equal(sooner, stock.SoonestExpiry);
    }

    [Fact(DisplayName = "LoadAsync does not include depleted stock lots")]
    public async Task LoadAsync_ExcludesDepleted_StockLots()
    {
        var productId = await SeedProductAsync("Eggs", _gramsId, trackStock: true);
        var locationId = await SeedLocationAsync("Fridge2");
        // One depleted lot, one active lot.
        await SeedStockEntryAsync(productId, locationId, 200m, _gramsId, expiryDate: null, depleted: true);
        await SeedStockEntryAsync(productId, locationId, 100m, _gramsId, expiryDate: null, depleted: false);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var stock = bag.GetStock(productId);
        Assert.NotNull(stock);
        var gramsLot = stock.Lots.FirstOrDefault(l => l.UnitId == _gramsId);
        Assert.NotNull(gramsLot);
        // Only the active lot (100g) counts — depleted lot excluded.
        Assert.Equal(100m, gramsLot.TotalQuantity);
    }

    [Fact(DisplayName = "LoadAsync does not include non-depleted zero-quantity stock lots")]
    public async Task LoadAsync_ExcludesZeroQuantity_StockLots()
    {
        var productId = await SeedProductAsync("Salt", _gramsId, trackStock: true);
        var locationId = await SeedLocationAsync("Cupboard3");
        // One active lot with zero quantity (not depleted but empty), one with real quantity.
        await SeedStockEntryAsync(productId, locationId, 0m, _gramsId, expiryDate: new DateOnly(2025, 1, 1), depleted: false);
        await SeedStockEntryAsync(productId, locationId, 50m, _gramsId, expiryDate: null, depleted: false);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var stock = bag.GetStock(productId);
        Assert.NotNull(stock);
        var gramsLot = stock.Lots.FirstOrDefault(l => l.UnitId == _gramsId);
        Assert.NotNull(gramsLot);
        // Only the non-zero lot (50g) counts — zero-qty lot excluded.
        Assert.Equal(50m, gramsLot.TotalQuantity);
        // SoonestExpiry from the zero-qty lot must NOT pollute the result.
        Assert.Null(stock.SoonestExpiry);
    }

    // ── price loading ────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync returns latest price per product from pricing schema")]
    public async Task LoadAsync_Returns_LatestPrice_FromPricingSchema()
    {
        var productId = await SeedProductAsync("Oats", _gramsId);
        var older = DateTime.UtcNow.AddDays(-7);
        var newer = DateTime.UtcNow.AddDays(-1);
        await SeedPriceObservationAsync(productId, 2.50m, 500m, _gramsId, unitPrice: 0.005m, observedAt: older);
        await SeedPriceObservationAsync(productId, 3.00m, 500m, _gramsId, unitPrice: 0.006m, observedAt: newer);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var price = bag.GetLatestPrice(productId);
        Assert.NotNull(price);
        // Should return the newer observation (3.00, not 2.50).
        Assert.Equal(3.00m, price.Price);
        Assert.Equal(0.006m, price.UnitPrice);
    }

    [Fact(DisplayName = "LoadAsync returns null price when no price history exists")]
    public async Task LoadAsync_Returns_NullPrice_WhenNoPriceHistory()
    {
        var productId = await SeedProductAsync("Vinegar", _gramsId);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        Assert.Null(bag.GetLatestPrice(productId));
    }

    // ── unit and conversion loading ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync returns all household units from catalog schema")]
    public async Task LoadAsync_Returns_AllUnits_FromCatalogSchema()
    {
        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], []);

        // Both seeded units (g and kg) should be present.
        Assert.Contains(_gramsId, bag.Units.Keys);
        Assert.Contains(_kgId, bag.Units.Keys);
        Assert.Equal("g", bag.Units[_gramsId].Code);
        Assert.Equal("kg", bag.Units[_kgId].Code);
    }

    [Fact(DisplayName = "LoadAsync returns product conversions from catalog schema")]
    public async Task LoadAsync_Returns_ProductConversions_FromCatalogSchema()
    {
        var productId = await SeedProductAsync("Honey", _gramsId);
        // Add a conversion: 1 kg honey = 1350 g (density).
        await SeedConversionAsync(productId, _kgId, _gramsId, 1350m);

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        var conversions = bag.GetConversions(productId);
        Assert.Single(conversions);
        Assert.Equal(_kgId, conversions[0].FromUnitId);
        Assert.Equal(_gramsId, conversions[0].ToUnitId);
        Assert.Equal(1350m, conversions[0].Factor);
    }

    // ── cross-schema: ingredient product ids gathered automatically ──────────────────────────────

    [Fact(DisplayName = "LoadAsync gathers product ids from ingredient list automatically")]
    public async Task LoadAsync_GathersIngredientProductIds_Automatically()
    {
        // Recipe with an ingredient — caller only passes the recipe id, not the product id.
        // The read model must gather the ingredient's product id and load its product fact.
        var productId = await SeedProductAsync("Salt", _gramsId);
        var recipeId = await SeedRecipeAsync("Salted water", 2, (productId, 5m, _gramsId, 1));

        var rm = NewReadModel(_household);
        // Pass only the recipeId, NOT the productId explicitly.
        var bag = await rm.LoadAsync([recipeId], []);

        Assert.True(bag.Products.ContainsKey(productId),
            "Product referenced by recipe ingredient should be loaded without explicit caller seeding.");
    }

    // ── O(1) lookup helpers ──────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "WeekBag.GetRecipe returns null for unknown recipe id")]
    public async Task WeekBag_GetRecipe_ReturnsNull_WhenNotLoaded()
    {
        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], []);

        Assert.Null(bag.GetRecipe(Guid.NewGuid()));
    }

    [Fact(DisplayName = "WeekBag.GetStock returns null when product has no active stock")]
    public async Task WeekBag_GetStock_ReturnsNull_WhenNoActiveStock()
    {
        var productId = await SeedProductAsync("Pepper", _gramsId);
        // No stock seeded.

        var rm = NewReadModel(_household);
        var bag = await rm.LoadAsync([], [productId]);

        Assert.Null(bag.GetStock(productId));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private MealPlanWeekReadModel NewReadModel(HouseholdId household)
    {
        var tenant = new TenantContext();
        tenant.Set(household.Value);
        return new MealPlanWeekReadModel(db.ConnectionString, tenant);
    }

    private CatalogDbContext NewCatalogDb(HouseholdId household)
    {
        var opts = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new CatalogDbContext(opts);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private RecipesDbContext NewRecipesDb()
    {
        var opts = new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new RecipesDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private InventoryDbContext NewInventoryDb()
    {
        var opts = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new InventoryDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private PricingDbContext NewPricingDb()
    {
        var opts = new DbContextOptionsBuilder<PricingDbContext>().UseNpgsql(db.ConnectionString).Options;
        var ctx = new PricingDbContext(opts);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    /// <summary>Seeds a product into catalog.products; returns the product id as Guid.</summary>
    private async Task<Guid> SeedProductAsync(string name, Guid defaultUnitId, bool trackStock = true)
    {
        await using var catalog = NewCatalogDb(_household);
        var unitId = UnitId.From(defaultUnitId);
        var product = Product.Create(_household, name, unitId, Clock, trackStock: trackStock);
        await catalog.Products.AddAsync(product);
        await catalog.SaveChangesAsync();
        return product.Id.Value;
    }

    /// <summary>Seeds a recipe with the given ingredients into recipes.recipe + recipe_ingredient.</summary>
    private async Task<Guid> SeedRecipeAsync(
        string name,
        int defaultServings,
        params (Guid ProductId, decimal Quantity, Guid UnitId, int Ordinal)[] ingredients)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // INSERT into recipes.recipe
        var recipeId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO recipes.recipe
                    (recipe_id, household_id, name, default_servings, created_at, updated_at)
                VALUES
                    (@id, @hid, @name, @servings, NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", recipeId);
            cmd.Parameters.AddWithValue("hid", _household.Value);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("servings", defaultServings);
            await cmd.ExecuteNonQueryAsync();
        }

        // INSERT recipe_ingredient rows
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

    /// <summary>Seeds a location into catalog.locations; returns the location id.</summary>
    private async Task<Guid> SeedLocationAsync(string name)
    {
        await using var catalog = NewCatalogDb(_household);
        var location = Location.Create(_household, name, LocationType.Ambient);
        await catalog.Locations.AddAsync(location);
        await catalog.SaveChangesAsync();
        return location.Id.Value;
    }

    /// <summary>Seeds an inventory stock entry; returns the entry id.</summary>
    private async Task SeedStockEntryAsync(
        Guid productId,
        Guid locationId,
        decimal quantity,
        Guid unitId,
        DateOnly? expiryDate,
        bool depleted = false)
    {
        // Ensure the product_stock root row exists.
        await EnsureProductStockAsync(productId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        var depletedAt = depleted ? (object)DateTime.UtcNow : DBNull.Value;
        var expiryObj = expiryDate.HasValue ? (object)expiryDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

        cmd.CommandText = """
            INSERT INTO inventory.stock_entry
                (entry_id, household_id, product_id, location_id, quantity, unit_id, expiry_date,
                 is_open, created_at, updated_at, depleted_at, purchased_at)
            VALUES
                (@id, @hid, @pid, @lid, @qty, @uid, @exp,
                 false, NOW(), NOW(), @dep, NOW())
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("lid", locationId);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("exp", expiryObj);
        cmd.Parameters.AddWithValue("dep", depletedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureProductStockAsync(Guid productId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inventory.product_stock (household_id, product_id, created_at, updated_at)
            VALUES (@hid, @pid, NOW(), NOW())
            ON CONFLICT (household_id, product_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds a price observation into pricing.price_observation.</summary>
    private async Task SeedPriceObservationAsync(
        Guid productId,
        decimal price,
        decimal quantity,
        Guid unitId,
        decimal? unitPrice,
        DateTime observedAt)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        var unitPriceObj = unitPrice.HasValue ? (object)unitPrice.Value : DBNull.Value;

        cmd.CommandText = """
            INSERT INTO pricing.price_observation
                (observation_id, household_id, product_id, price, quantity, unit_id, unit_price,
                 source, source_ref, observed_at, user_id)
            VALUES
                (@id, @hid, @pid, @price, @qty, @uid, @up,
                 'Purchase', @ref, @obs, @usr)
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("hid", _household.Value);
        cmd.Parameters.AddWithValue("pid", productId);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("uid", unitId);
        cmd.Parameters.AddWithValue("up", unitPriceObj);
        cmd.Parameters.AddWithValue("ref", Guid.NewGuid());
        cmd.Parameters.AddWithValue("obs", observedAt);
        cmd.Parameters.AddWithValue("usr", Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds a product conversion into catalog.product_conversions.</summary>
    private async Task SeedConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor)
    {
        await using var catalog = NewCatalogDb(_household);
        // Use raw SQL to insert the conversion directly (ProductConversion entity doesn't have a
        // public static factory in this project — conversions are managed via Product.AddConversion).
        await catalog.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.product_conversions
                (id, household_id, product_id, from_unit_id, to_unit_id, factor)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5})
            """,
            Guid.NewGuid(), _household.Value, productId, fromUnitId, toUnitId, factor);
    }
}
