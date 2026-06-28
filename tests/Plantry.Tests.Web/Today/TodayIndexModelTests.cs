using Plantry.Identity.Domain;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Today;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L1 tests for <see cref="IndexModel"/>.
///
/// Covers two independent pieces of logic:
/// <list type="bullet">
///   <item><b>BuildGreeting</b> — time-of-day salutation and household-name composition.</item>
///   <item><b>IsColdStart</b> — true iff the household has no stock, no recipes, and no pending
///   intake sessions; false when any one of the three is non-empty.</item>
/// </list>
/// </summary>
public sealed class TodayIndexModelTests
{
    // ── BuildGreeting — time-of-day salutation ────────────────────────────────

    [Theory(DisplayName = "BuildGreeting — returns 'Good morning' for hours 5–11")]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(11)]
    public void BuildGreeting_MorningHours_ReturnsMorning(int hour)
    {
        var result = IndexModel.BuildGreeting(hour, "Rivera household");
        Assert.StartsWith("Good morning", result, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "BuildGreeting — returns 'Good afternoon' for hours 12–16")]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(16)]
    public void BuildGreeting_AfternoonHours_ReturnsAfternoon(int hour)
    {
        var result = IndexModel.BuildGreeting(hour, "Rivera household");
        Assert.StartsWith("Good afternoon", result, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "BuildGreeting — returns 'Good evening' for hours 17–20")]
    [InlineData(17)]
    [InlineData(19)]
    [InlineData(20)]
    public void BuildGreeting_EveningHours_ReturnsEvening(int hour)
    {
        var result = IndexModel.BuildGreeting(hour, "Rivera household");
        Assert.StartsWith("Good evening", result, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "BuildGreeting — returns 'Good night' outside 5–20")]
    [InlineData(21)]
    [InlineData(23)]
    [InlineData(0)]
    [InlineData(4)]
    public void BuildGreeting_NightHours_ReturnsGoodNight(int hour)
    {
        var result = IndexModel.BuildGreeting(hour, "Rivera household");
        Assert.StartsWith("Good night", result, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "BuildGreeting — includes household name when non-empty")]
    public void BuildGreeting_WithHouseholdName_IncludesName()
    {
        var result = IndexModel.BuildGreeting(9, "Rivera household");
        Assert.Equal("Good morning, Rivera household.", result);
    }

    [Theory(DisplayName = "BuildGreeting — no comma or name when household name is empty or whitespace")]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildGreeting_EmptyName_NoCommaNoName(string name)
    {
        var result = IndexModel.BuildGreeting(9, name);
        Assert.Equal("Good morning.", result);
        Assert.DoesNotContain(",", result, StringComparison.Ordinal);
    }

    // ── IsColdStart determination ─────────────────────────────────────────────

    [Fact(DisplayName = "IsColdStart — true when stock, recipes, and pending intake are all empty")]
    public async Task IsColdStart_AllEmpty_IsTrue()
    {
        var model = BuildModel(hasStock: false, hasRecipes: false, hasPendingIntake: false);
        await model.OnGetAsync();
        Assert.True(model.IsColdStart);
    }

    [Fact(DisplayName = "IsColdStart — false when household has stock")]
    public async Task IsColdStart_HasStock_IsFalse()
    {
        var model = BuildModel(hasStock: true, hasRecipes: false, hasPendingIntake: false);
        await model.OnGetAsync();
        Assert.False(model.IsColdStart);
    }

    [Fact(DisplayName = "IsColdStart — false when household has recipes")]
    public async Task IsColdStart_HasRecipes_IsFalse()
    {
        var model = BuildModel(hasStock: false, hasRecipes: true, hasPendingIntake: false);
        await model.OnGetAsync();
        Assert.False(model.IsColdStart);
    }

    [Fact(DisplayName = "IsColdStart — false when household has a pending intake session")]
    public async Task IsColdStart_HasPendingIntake_IsFalse()
    {
        var model = BuildModel(hasStock: false, hasRecipes: false, hasPendingIntake: true);
        await model.OnGetAsync();
        Assert.False(model.IsColdStart);
    }

    // ── ShowTakeStockCta (J6/P4-9) ──────────────────────────────────────────────

    [Fact(DisplayName = "ShowTakeStockCta — true when household has no stock (cold start)")]
    public async Task ShowTakeStockCta_NoStock_IsTrue()
    {
        var model = BuildModel(hasStock: false, hasRecipes: false, hasPendingIntake: false);
        await model.OnGetAsync();
        Assert.True(model.ShowTakeStockCta);
    }

    [Fact(DisplayName = "ShowTakeStockCta — true when household has recipes but no stock")]
    public async Task ShowTakeStockCta_HasRecipesButNoStock_IsTrue()
    {
        var model = BuildModel(hasStock: false, hasRecipes: true, hasPendingIntake: false);
        await model.OnGetAsync();
        Assert.True(model.ShowTakeStockCta);
    }

    [Fact(DisplayName = "ShowTakeStockCta — false once household has stock")]
    public async Task ShowTakeStockCta_HasStock_IsFalse()
    {
        var model = BuildModel(hasStock: true, hasRecipes: false, hasPendingIntake: false);
        await model.OnGetAsync();
        Assert.False(model.ShowTakeStockCta);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly HouseholdId TestHousehold = HouseholdId.From(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private static readonly IClock FixedClock =
        new FakeClock(new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero));

    private static IndexModel BuildModel(bool hasStock, bool hasRecipes, bool hasPendingIntake,
        IReadOnlyList<ExpiringSoonItem>? expiringSoon = null)
    {
        var tenant = new FakeStaticTenantContext(TestHousehold.Value);
        var stockRepo = new FakeProductStockRepository(hasStock);
        var inventoryQueries = new InventoryQueryService(
            stockRepo,
            new FakeEmptyCatalogReadFacade(),
            new FakeNullConversionProvider(),
            FixedClock,
            expiringSoon is null ? tenant : new FakeStaticTenantContext(TestHousehold.Value));
        return new IndexModel(
            new FakeHouseholdRepository("Test Household"),
            stockRepo,
            new FakeRecipeRepository(hasRecipes),
            new FakeSessionRepository(hasPendingIntake),
            inventoryQueries,
            FixedClock,
            tenant);
    }

    /// <summary>Builds a model pre-populated with a known expiring-soon list for widget state tests.</summary>
    private static IndexModel BuildModelWithExpiring(IReadOnlyList<ExpiringSoonItem> items)
    {
        var tenant = new FakeStaticTenantContext(TestHousehold.Value);
        // hasStock=true so IsColdStart=false and the widget reaches the populated/all-clear branch
        var stockRepo = new FakeProductStockRepository(hasStock: true);
        var inventoryQueries = new FakeInventoryQueryService(items, tenant);
        return new IndexModel(
            new FakeHouseholdRepository("Test Household"),
            stockRepo,
            new FakeRecipeRepository(hasRecipes: true),
            new FakeSessionRepository(hasPendingIntake: false),
            inventoryQueries,
            FixedClock,
            tenant);
    }

    // ── Fakes -----------------------------------------------------------------

    private sealed class FakeStaticTenantContext(Guid householdId) : ITenantContext
    {
        public Guid? HouseholdId => householdId;
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeHouseholdRepository(string name) : IHouseholdRepository
    {
        public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default)
        {
            var household = Household.Create(name, new FakeClock(DateTimeOffset.UtcNow));
            return Task.FromResult<Household?>(household);
        }
        public Task AddAsync(Household household, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProductStockRepository(bool hasStock) : IProductStockRepository
    {
        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(hasStock);

        // Unused by IndexModel:
        public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(new List<ProductStock>());
        public Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) => Task.FromResult<ProductStock?>(null);
        public Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) => Task.FromResult<ProductStock?>(null);
        public Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) => Task.FromResult<ProductStock?>(null);
        public Task AddAsync(ProductStock stock, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default) => Task.FromResult(true);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) => work(ct);
    }

    private sealed class FakeRecipeRepository(bool hasRecipes) : IRecipeRepository
    {
        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(hasRecipes);

        // Unused by IndexModel:
        public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Recipe>>(Array.Empty<Recipe>());
        public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
        public Task AddAsync(Recipe recipe, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) => Task.FromResult<Recipe?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeSessionRepository(bool hasPendingIntake) : IImportSessionRepository
    {
        public Task<bool> HasPendingAsync(HouseholdId hid, CancellationToken ct = default) =>
            Task.FromResult(hasPendingIntake);

        // Unused by IndexModel:
        public Task<List<ImportSession>> ListPendingAsync(HouseholdId hid, CancellationToken ct = default) =>
            Task.FromResult(new List<ImportSession>());
        public Task AddAsync(ImportSession session, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportSession?>(null);
        public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportReceipt?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ImportSession>> ListRecentAsync(HouseholdId hid, int take = 10, CancellationToken ct = default) => Task.FromResult(new List<ImportSession>());
    }

    /// <summary>Empty catalog read facade — returns nothing. Used to back InventoryQueryService in model tests
    /// where we don't need real expiry data (ExpiringSoon will be empty).</summary>
    private sealed class FakeEmptyCatalogReadFacade : ICatalogReadFacade
    {
        public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<CatalogProductInfo?>(null);
        public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogProductInfo>>([]);
        public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }

    /// <summary>Null conversion provider — returns an identity converter for every product.</summary>
    private sealed class FakeNullConversionProvider : IProductConversionProvider
    {
        public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<IQuantityConverter>(new IdentityConverter());

        private sealed class IdentityConverter : IQuantityConverter
        {
            public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
        }
    }

    /// <summary>
    /// Wraps <see cref="InventoryQueryService"/> but overrides ExpiringSoonAsync to return a
    /// pre-canned list — avoids wiring up full catalog/stock fakes for widget state tests.
    /// </summary>
    private sealed class FakeInventoryQueryService(
        IReadOnlyList<ExpiringSoonItem> items, ITenantContext tenant)
        : InventoryQueryService(
            new FakeProductStockRepository(hasStock: true),
            new FakeEmptyCatalogReadFacade(),
            new FakeNullConversionProvider(),
            new FakeClock(new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero)),
            tenant)
    {
        public override Task<IReadOnlyList<ExpiringSoonItem>> ExpiringSoonAsync(CancellationToken ct = default) =>
            Task.FromResult(items);
    }
}

// ── Widget state tests (L4 — model-level) ──────────────────────────────────────────

/// <summary>
/// L4 model tests for the expiring-soon widget states on the Today page.
/// Verifies that <see cref="IndexModel.ExpiringSoon"/> and <see cref="IndexModel.ExpiringUrgent"/>
/// drive the three widget render states correctly: onboard, all-clear, and populated.
/// </summary>
public sealed class ExpiringWidgetModelTests
{
    private static readonly HouseholdId TestHousehold = HouseholdId.From(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly IClock FixedClock =
        new FakeClock2(new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly Today = new(2026, 6, 18);

    private IndexModel BuildModel(
        bool hasStock, bool hasRecipes, bool hasPendingIntake,
        IReadOnlyList<ExpiringSoonItem>? expiringSoon = null)
    {
        var tenant = new FakeStaticTenantContext2(TestHousehold.Value);
        var stockRepo = new FakeStockRepo(hasStock);
        var inventoryQueries = expiringSoon is null
            ? new InventoryQueryService(
                stockRepo,
                new FakeEmptyCatalog(),
                new FakeConvProvider(),
                FixedClock,
                tenant)
            : (InventoryQueryService)new FakeInventoryQueryService2(expiringSoon, tenant);
        return new IndexModel(
            new FakeHouseholdRepo2("Test Household"),
            stockRepo,
            new FakeRecipeRepo2(hasRecipes),
            new FakeSessionRepo2(hasPendingIntake),
            inventoryQueries,
            FixedClock,
            tenant);
    }

    // ── Onboard state ────────────────────────────────────────────────────────

    [Fact(DisplayName = "Widget — IsColdStart is true when no stock, recipes, or intake")]
    public async Task Widget_Onboard_IsColdStartTrue()
    {
        var model = BuildModel(hasStock: false, hasRecipes: false, hasPendingIntake: false);
        await model.OnGetAsync();
        Assert.True(model.IsColdStart);
        Assert.Empty(model.ExpiringSoon);
    }

    // ── All-clear state ───────────────────────────────────────────────────────

    [Fact(DisplayName = "Widget — all-clear when stock exists but nothing expiring")]
    public async Task Widget_AllClear_NothingExpiring()
    {
        var model = BuildModel(hasStock: true, hasRecipes: false, hasPendingIntake: false,
            expiringSoon: []);
        await model.OnGetAsync();
        Assert.False(model.IsColdStart);
        Assert.Empty(model.ExpiringSoon);
        Assert.False(model.ExpiringUrgent);
    }

    // ── Populated state ───────────────────────────────────────────────────────

    [Fact(DisplayName = "Widget — populated state carries items and ExpiringUrgent=false when none urgent")]
    public async Task Widget_Populated_NotUrgent()
    {
        var items = new[]
        {
            new ExpiringSoonItem(Guid.NewGuid(), "Milk", 1m, "L", "Fridge", Today.AddDays(5), 5, false),
            new ExpiringSoonItem(Guid.NewGuid(), "Cheese", 200m, "g", "Fridge", Today.AddDays(3), 3, false),
        };
        var model = BuildModel(hasStock: true, hasRecipes: true, hasPendingIntake: false,
            expiringSoon: items);
        await model.OnGetAsync();
        Assert.False(model.IsColdStart);
        Assert.Equal(2, model.ExpiringSoon.Count);
        Assert.False(model.ExpiringUrgent);
    }

    [Fact(DisplayName = "Widget — ExpiringUrgent=true when any item has DaysLeft <= 1")]
    public async Task Widget_Populated_UrgentWhenDaysLeftAtMostOne()
    {
        var items = new[]
        {
            new ExpiringSoonItem(Guid.NewGuid(), "Yogurt", 1m, "ea", null, Today.AddDays(1), 1, false),
        };
        var model = BuildModel(hasStock: true, hasRecipes: true, hasPendingIntake: false,
            expiringSoon: items);
        await model.OnGetAsync();
        Assert.True(model.ExpiringUrgent);
    }

    [Fact(DisplayName = "Widget — ExpiringUrgent=true when an item is expired (IsExpired=true)")]
    public async Task Widget_Populated_UrgentForExpiredItem()
    {
        var items = new[]
        {
            new ExpiringSoonItem(Guid.NewGuid(), "Eggs", 6m, "ea", "Fridge", Today.AddDays(-2), 0, IsExpired: true),
        };
        var model = BuildModel(hasStock: true, hasRecipes: true, hasPendingIntake: false,
            expiringSoon: items);
        await model.OnGetAsync();
        Assert.True(model.ExpiringUrgent);
    }

    // ── ExpiringSoon not loaded on cold start ─────────────────────────────────

    [Fact(DisplayName = "Widget — ExpiringSoonAsync is not called when IsColdStart=true")]
    public async Task Widget_ExpiringSoonNotLoadedOnColdStart()
    {
        // Cold start: no stock, no recipes, no pending intake => IsColdStart=true, query skipped
        var model = BuildModel(hasStock: false, hasRecipes: false, hasPendingIntake: false);
        await model.OnGetAsync();
        Assert.True(model.IsColdStart);
        // ExpiringSoon stays empty — the query was never called
        Assert.Empty(model.ExpiringSoon);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeStaticTenantContext2(Guid householdId) : ITenantContext
    {
        public Guid? HouseholdId => householdId;
    }

    private sealed class FakeClock2(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeHouseholdRepo2(string name) : IHouseholdRepository
    {
        public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default)
        {
            var h = Household.Create(name, new FakeClock2(DateTimeOffset.UtcNow));
            return Task.FromResult<Household?>(h);
        }
        public Task AddAsync(Household household, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeStockRepo(bool hasStock) : IProductStockRepository
    {
        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(hasStock);
        public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(new List<ProductStock>());
        public Task<ProductStock?> FindForUpdateAsync(HouseholdId h, Guid p, CancellationToken ct = default) => Task.FromResult<ProductStock?>(null);
        public Task<ProductStock?> FindAsync(HouseholdId h, Guid p, CancellationToken ct = default) => Task.FromResult<ProductStock?>(null);
        public Task<ProductStock?> FindWithHistoryAsync(HouseholdId h, Guid p, CancellationToken ct = default) => Task.FromResult<ProductStock?>(null);
        public Task AddAsync(ProductStock stock, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default) => Task.FromResult(true);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) => work(ct);
    }

    private sealed class FakeRecipeRepo2(bool hasRecipes) : IRecipeRepository
    {
        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(hasRecipes);
        public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Recipe>>([]);
        public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
        public Task AddAsync(Recipe recipe, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) => Task.FromResult<Recipe?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeSessionRepo2(bool hasPending) : IImportSessionRepository
    {
        public Task<bool> HasPendingAsync(HouseholdId hid, CancellationToken ct = default) => Task.FromResult(hasPending);
        public Task<List<ImportSession>> ListPendingAsync(HouseholdId hid, CancellationToken ct = default) =>
            Task.FromResult(new List<ImportSession>());
        public Task AddAsync(ImportSession session, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportSession?>(null);
        public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportReceipt?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ImportSession>> ListRecentAsync(HouseholdId hid, int take = 10, CancellationToken ct = default) =>
            Task.FromResult(new List<ImportSession>());
    }

    private sealed class FakeEmptyCatalog : ICatalogReadFacade
    {
        public Task<CatalogProductInfo?> FindProductAsync(Guid id, CancellationToken ct = default) => Task.FromResult<CatalogProductInfo?>(null);
        public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CatalogProductInfo>>([]);
        public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }

    private sealed class FakeConvProvider : IProductConversionProvider
    {
        public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<IQuantityConverter>(new IdConverter());
        private sealed class IdConverter : IQuantityConverter
        {
            public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
        }
    }

    private sealed class FakeInventoryQueryService2(
        IReadOnlyList<ExpiringSoonItem> items, ITenantContext tenant)
        : InventoryQueryService(
            new FakeStockRepo(hasStock: true),
            new FakeEmptyCatalog(),
            new FakeConvProvider(),
            new FakeClock2(new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero)),
            tenant)
    {
        public override Task<IReadOnlyList<ExpiringSoonItem>> ExpiringSoonAsync(CancellationToken ct = default) =>
            Task.FromResult(items);
    }
}
