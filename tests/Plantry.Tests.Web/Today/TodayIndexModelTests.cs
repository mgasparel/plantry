using Plantry.Identity.Domain;
using Plantry.Intake.Domain;
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly HouseholdId TestHousehold = HouseholdId.From(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private static IndexModel BuildModel(bool hasStock, bool hasRecipes, bool hasPendingIntake)
    {
        var tenant = new FakeStaticTenantContext(TestHousehold.Value);
        return new IndexModel(
            new FakeHouseholdRepository("Test Household"),
            new FakeProductStockRepository(hasStock),
            new FakeRecipeRepository(hasRecipes),
            new FakeSessionRepository(hasPendingIntake, TestHousehold),
            new FakeClock(new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero)),
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
        public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        {
            if (!hasStock) return Task.FromResult(new List<ProductStock>());
            // Return a one-element list so Count > 0 — IndexModel only checks .Count > 0.
            var sentinel = ProductStock.Start(householdId, Guid.NewGuid(),
                new FakeClock(DateTimeOffset.UtcNow));
            return Task.FromResult(new List<ProductStock> { sentinel });
        }

        // Unused by IndexModel:
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
        public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default)
        {
            if (!hasRecipes) return Task.FromResult<IReadOnlyList<Recipe>>(Array.Empty<Recipe>());
            // Return a one-element list so Count > 0 — IndexModel only checks .Count > 0.
            var clock = new FakeClock(DateTimeOffset.UtcNow);
            var recipe = Recipe.Create(TestHousehold, "Pasta", 2, clock).Value;
            return Task.FromResult<IReadOnlyList<Recipe>>(new[] { recipe });
        }
        public Task AddAsync(Recipe recipe, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) => Task.FromResult<Recipe?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeSessionRepository(bool hasPendingIntake, HouseholdId householdId) : IImportSessionRepository
    {
        public Task<List<ImportSession>> ListPendingAsync(HouseholdId hid, CancellationToken ct = default)
        {
            if (!hasPendingIntake) return Task.FromResult(new List<ImportSession>());
            // Return a one-element list so Count > 0 — IndexModel only checks .Count > 0.
            var clock = new FakeClock(DateTimeOffset.UtcNow);
            var session = ImportSession.Start(householdId, ImportSourceType.Receipt, Guid.NewGuid(), clock);
            return Task.FromResult(new List<ImportSession> { session });
        }
        public Task AddAsync(ImportSession session, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportSession?>(null);
        public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportReceipt?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ImportSession>> ListRecentAsync(HouseholdId hid, int take = 10, CancellationToken ct = default) => Task.FromResult(new List<ImportSession>());
    }
}
