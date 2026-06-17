using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Infrastructure;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Tests.Integration.Shopping;
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests for <see cref="ShopForWeekService"/> — the "Shop for this week" J6 flow
/// end-to-end through the real <see cref="MealPlanShoppingWriterAdapter"/> and Shopping persistence.
///
/// Covers:
/// - L3-a: missing recipe ingredient is written to the shopping list with source=meal_plan.
/// - L3-b: running ShopForWeek twice merges quantities (no duplicate rows) per the
///         Shopping merge rule (AddItemCommand, intentionalDuplicate=false, keyed on
///         ProductId &amp;&amp; !IsChecked).
/// - L3-c: fully-stocked week adds nothing (0 items returned, list unchanged).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ShopForWeekIntegrationTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private MealSlotId _slotId = MealSlotId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Monday = new(2026, 6, 8); // a known Monday (June 8, 2026 is Monday)

    // Soft-ref IDs — these are never inserted into Catalog; Shopping stores them as raw Guids.
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _recipeId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _slotId = MealSlotId.New();

        // Seed the household's shopping list (ShoppingList aggregate must exist).
        await using var shopCtx = NewShoppingDb();
        var seeder = new ShoppingReferenceDataSeeder(shopCtx, Clock);
        await seeder.SeedAsync(_household);

        // Seed the MealSlotConfig so the FK constraint on planned_meal is satisfied.
        await SeedSlotConfigAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── L3-a: missing recipe ingredient is written to the shopping list ──────

    [Fact(DisplayName = "L3-a — ShopForWeek writes missing recipe ingredient to shopping list (source=meal_plan)")]
    public async Task ShopForWeek_WritesMissingIngredientToShoppingList()
    {
        // Seed a plan with a recipe dish (recipe is identified by soft-ref GUID — no DB row needed in Recipes).
        await SeedMealPlanAsync(MakeSingleRecipePlan(servings: 2));

        var (mealPlanRepo, shopWriter) = BuildAdapters();
        var recipeReader = new FakeMissingReader(_recipeId,
            [new RecipeMissingIngredient(_productId, 1.5m, _unitId)]);
        var stockReader = new NullMealPlanStockReader();

        var svc = new ShopForWeekService(mealPlanRepo, recipeReader, stockReader, shopWriter);
        var result = await svc.ExecuteAsync(_household, Monday);

        Assert.Equal(1, result.ItemsAdded);

        // Reload shopping list from DB and assert item exists with source=meal_plan
        await using var shopCtx = NewShoppingDb();
        var list = await shopCtx.ShoppingLists.Include(l => l.Items).FirstAsync();
        var item = Assert.Single(list.Items);
        Assert.Equal(_productId, item.ProductId);
        Assert.Equal(1.5m, item.Quantity);
        Assert.Equal(Plantry.Shopping.Domain.ItemSource.MealPlan, item.Source);
    }

    // ── L3-b: running ShopForWeek twice merges quantities (no duplicates) ────

    [Fact(DisplayName = "L3-b — ShopForWeek called twice merges into one row (no duplicate lines)")]
    public async Task ShopForWeek_CalledTwice_MergesQuantity()
    {
        // Seed a plan with a recipe dish missing 1.5 units of the product.
        await SeedMealPlanAsync(MakeSingleRecipePlan(servings: 2));

        var (mealPlanRepo, shopWriter) = BuildAdapters();
        var recipeReader = new FakeMissingReader(_recipeId,
            [new RecipeMissingIngredient(_productId, 1.5m, _unitId)]);
        var stockReader = new NullMealPlanStockReader();

        var svc = new ShopForWeekService(mealPlanRepo, recipeReader, stockReader, shopWriter);

        // First shop
        var r1 = await svc.ExecuteAsync(_household, Monday);
        Assert.Equal(1, r1.ItemsAdded);

        // Second shop — same product, same quantity (still missing)
        var (mealPlanRepo2, shopWriter2) = BuildAdapters();
        var svc2 = new ShopForWeekService(mealPlanRepo2, recipeReader, stockReader, shopWriter2);
        var r2 = await svc2.ExecuteAsync(_household, Monday);
        Assert.Equal(1, r2.ItemsAdded);

        // Reload — must be exactly ONE row, with merged quantity (1.5 + 1.5 = 3.0)
        await using var shopCtx = NewShoppingDb();
        var list = await shopCtx.ShoppingLists.Include(l => l.Items).FirstAsync();
        var item = Assert.Single(list.Items);  // NOT two rows
        Assert.Equal(3.0m, item.Quantity);     // merged, not duplicated
    }

    // ── L3-c: fully-stocked week — nothing added ─────────────────────────────

    [Fact(DisplayName = "L3-c — ShopForWeek returns 0 items when recipe has no missing ingredients")]
    public async Task ShopForWeek_FullyStocked_AddsNothing()
    {
        await SeedMealPlanAsync(MakeSingleRecipePlan(servings: 2));

        var (mealPlanRepo, shopWriter) = BuildAdapters();
        // Empty missing list — all ingredients in stock
        var recipeReader = new FakeMissingReader(_recipeId, []);
        var stockReader = new NullMealPlanStockReader();

        var svc = new ShopForWeekService(mealPlanRepo, recipeReader, stockReader, shopWriter);
        var result = await svc.ExecuteAsync(_household, Monday);

        Assert.Equal(0, result.ItemsAdded);

        await using var shopCtx = NewShoppingDb();
        var list = await shopCtx.ShoppingLists.Include(l => l.Items).FirstAsync();
        Assert.Empty(list.Items);
    }

    // ── L3-d: never-stocked product dish written to shopping list ────────────

    [Fact(DisplayName = "L3-d — ShopForWeek writes never-stocked product dish (zero-qty snapshot from adapter)")]
    public async Task ShopForWeek_WritesProductDish_WhenNeverStocked_ZeroQtySnapshot()
    {
        // A product dish plan (not a recipe — the product itself is the dish).
        var productPlan = MealPlan.Start(_household, Monday, Clock);
        productPlan.AssignMeal(Monday, _slotId,
            [new DishSpec(DishKind.Product, _productId, 2)],
            null, "manual", Guid.Empty, Clock);
        await SeedMealPlanAsync(productPlan);

        var (mealPlanRepo, shopWriter) = BuildAdapters();
        var recipeReader = new FakeMissingReader(_recipeId, []); // no recipe dishes
        // ZeroStockReader simulates the fixed MealPlanStockReaderAdapter behaviour:
        // returns 0 available + the real DefaultUnitId (was null before fix, causing Guid.Empty drop).
        var stockReader = new ZeroStockReader(_productId, _unitId);

        var svc = new ShopForWeekService(mealPlanRepo, recipeReader, stockReader, shopWriter);
        var result = await svc.ExecuteAsync(_household, Monday);

        Assert.Equal(1, result.ItemsAdded);

        await using var shopCtx = NewShoppingDb();
        var list = await shopCtx.ShoppingLists.Include(l => l.Items).FirstAsync();
        var item = Assert.Single(list.Items);
        Assert.Equal(_productId, item.ProductId);
        Assert.Equal(2m, item.Quantity); // all 2 servings needed (0 available)
        Assert.Equal(Plantry.Shopping.Domain.ItemSource.MealPlan, item.Source);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private (MealPlanRepository mealPlanRepo, MealPlanShoppingWriterAdapter shopWriter) BuildAdapters()
    {
        var mealPlanCtx = NewMealPlanningDb();
        var mealPlanRepo = new MealPlanRepository(mealPlanCtx);

        var shopCtx = NewShoppingDb();
        var shopRepo = new ShoppingListRepository(shopCtx);
        var tenant = new SimpleTenantContext(_household.Value);
        var shopWriter = new MealPlanShoppingWriterAdapter(
            shopRepo,
            NullShoppingCatalogReader.Instance,
            Clock,
            tenant);

        return (mealPlanRepo, shopWriter);
    }

    private MealPlan MakeSingleRecipePlan(int servings)
    {
        var plan = MealPlan.Start(_household, Monday, Clock);
        plan.AssignMeal(Monday, _slotId,
            [new DishSpec(DishKind.Recipe, _recipeId, servings)],
            null, "manual", Guid.Empty, Clock);
        return plan;
    }

    private async Task SeedMealPlanAsync(MealPlan plan)
    {
        await using var ctx = NewMealPlanningDb();
        var repo = new MealPlanRepository(ctx);
        var existing = await repo.FindByWeekAsync(_household, Monday);
        if (existing is not null) return; // already seeded

        // MealPlanRepository.FindOrCreateAsync creates-and-saves; use it to avoid direct ctx add.
        // But we need a plan with a specific meal — use a bypass: add via EF directly.
        ctx.MealPlans.Add(plan);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedSlotConfigAsync()
    {
        await using var ctx = new MealPlanningDbContext(MealPlanningOptions());
        var configId = Guid.NewGuid();
        // Insert via raw SQL to bypass query filter (EF filter requires household to be set,
        // but seed operations use the superuser connection which bypasses RLS).
        // Use parameterized form ({0}, {1}, ...) matching MealPlanPersistenceTests.SeedSlotConfigAsync.
        // meal_slot_config has created_at/updated_at; meal_slot does NOT (matches DB schema).
        // The '{{}}' in the raw string literal escapes to '{}' for the Postgres JSON empty array.
        await ctx.Database.ExecuteSqlRawAsync(@"
            INSERT INTO meal_planning.meal_slot_config (meal_slot_config_id, household_id, created_at, updated_at)
            VALUES ({0}, {1}, NOW(), NOW());
            INSERT INTO meal_planning.meal_slot (meal_slot_id, household_id, meal_slot_config_id, label, ordinal, default_attendees)
            VALUES ({2}, {1}, {0}, 'Dinner', 1, '{{}}');",
            configId, _household.Value, _slotId.Value);
    }

    private DbContextOptions<MealPlanningDbContext> MealPlanningOptions() =>
        new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(db.ConnectionString).Options;

    private MealPlanningDbContext NewMealPlanningDb()
    {
        var ctx = new MealPlanningDbContext(MealPlanningOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private DbContextOptions<ShoppingDbContext> ShoppingOptions() =>
        new DbContextOptionsBuilder<ShoppingDbContext>().UseNpgsql(db.ConnectionString).Options;

    private ShoppingDbContext NewShoppingDb()
    {
        var ctx = new ShoppingDbContext(ShoppingOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class SimpleTenantContext(Guid householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }
}

// ── test doubles ──────────────────────────────────────────────────────────────

internal sealed class FakeMissingReader(
    Guid recipeId,
    IReadOnlyList<RecipeMissingIngredient> missing) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RecipeReadModel?>(null);

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeReadModel>>([]);

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid id, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid id, int servings, CancellationToken ct = default)
        => Task.FromResult(id == recipeId ? missing : (IReadOnlyList<RecipeMissingIngredient>)[]);
}

internal sealed class NullMealPlanStockReader : IMealPlanStockReader
{
    public Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<MealPlanProductStock?>(null);
}

/// <summary>
/// Simulates the fixed <c>MealPlanStockReaderAdapter</c> behaviour for a never-stocked product:
/// returns a zero-quantity snapshot with the product's catalog DefaultUnitId (not null, not Guid.Empty).
/// Before the fix, the adapter returned null → Guid.Empty → ShopForWeekService silently dropped the dish.
/// </summary>
internal sealed class ZeroStockReader(Guid knownProductId, Guid defaultUnitId) : IMealPlanStockReader
{
    public Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
    {
        if (productId == knownProductId)
            return Task.FromResult<MealPlanProductStock?>(
                new MealPlanProductStock(productId, 0m, defaultUnitId, null));
        return Task.FromResult<MealPlanProductStock?>(null);
    }
}
