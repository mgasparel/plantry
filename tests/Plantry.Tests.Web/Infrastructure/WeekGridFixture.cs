using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Plantry.Identity.Infrastructure;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.MealPlanning;

namespace Plantry.Tests.Web.Infrastructure;

// Relocated from MealPlanning/WeekGridFragmentTests.cs, MealPlanning/ConflictCellFragmentTests.cs, and
// Preferences/PreferencesOobFragmentTests.cs (plantry-ej84) — these are the declarations
// MealPlanFragmentFactory's defaults reach back into. Co-locating them here converges Infrastructure/
// on the house convention the other 12 shared factories already follow (fakes live alongside the
// factory that defaults to them, never reached for across a feature-namespace `using`). Pure
// declaration move: no behaviour change, no assertion change.

// ── Fixture ───────────────────────────────────────────────────────────────────

public static class WeekGridFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private static readonly HouseholdId HhId = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    private static readonly IClock Clock = new FixedClock(MealPlanningTestClock.Instant);

    /// <summary>Shared singleton config so slot IDs are stable within a test run.</summary>
    public static readonly MealSlotConfig SharedConfig = MealSlotConfig.CreateWithDefaults(HhId, Clock);

    public static readonly Guid RecipeId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    public static readonly IReadOnlyList<HouseholdMember> Members =
        [new HouseholdMember(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), "Alice", "A")];

    public static readonly IReadOnlyList<RecipeReadModel> Recipes =
        [new RecipeReadModel(RecipeId, "Pasta Bolognese", [], 4)];

    public static readonly IReadOnlyList<MealPlanProductReadModel> Products = [];
}

// ── WAF test doubles ──────────────────────────────────────────────────────────

internal sealed class FakeMealPlanRepo : IMealPlanRepository
{
    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(
        IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(null);

    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(MealPlan.Start(householdId, weekStart, clock));

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeSlotRepo(MealSlotConfig? config) : IMealSlotConfigRepository
{
    public Task<MealSlotConfig?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult(config);

    public Task AddAsync(MealSlotConfig c, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeMemberReader(IReadOnlyList<HouseholdMember> members) : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default)
        => Task.FromResult(members);
}

internal sealed class FakeRecipeReader(IReadOnlyList<RecipeReadModel> recipes) : IRecipeReadModel
{
    public Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
        => Task.FromResult(recipes.FirstOrDefault(r => r.RecipeId == recipeId));

    public Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
    {
        var results = recipes
            .Where(r => r.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
        return Task.FromResult<IReadOnlyList<RecipeReadModel>>(results);
    }

    public Task<RecipeDishEnrichment?> GetEnrichmentAsync(Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
        => Task.FromResult<RecipeDishEnrichment?>(null);

    public Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(Guid recipeId, int servings, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeMissingIngredient>>([]);

    /// <summary>
    /// Targeted full-corpus query: returns true when ANY recipe in the in-memory list carries the tag.
    /// Unlike SearchAsync (which respects maxResults), this queries ALL recipes — no cap.
    /// </summary>
    public Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
        => Task.FromResult(recipes.Any(r => r.TagIds.Contains(tagId)));
}

internal sealed class FakeProductReader(IReadOnlyList<MealPlanProductReadModel> products) : IMealPlanCatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(products.Any(p => p.ProductId == productId));

    public Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(products.Any(p => p.ProductId == productId));

    public Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(string nameQuery, int maxResults, CancellationToken ct = default)
    {
        var results = products
            .Where(p => p.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
        return Task.FromResult<IReadOnlyList<MealPlanProductReadModel>>(results);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = productIds
            .Where(id => products.Any(p => p.ProductId == id))
            .ToDictionary(id => id, id => products.First(p => p.ProductId == id).Name);
        return Task.FromResult(result);
    }
}

// ── P3-6a null stubs (no-op implementations for WAF factories that don't test AI generation) ────

internal sealed class NullMealPlanner : IMealPlanner
{
    public Task<IReadOnlyList<ProposedMeal>> ProposeWeekAsync(
        IReadOnlyList<PlannerMealSlotContext> slotsContext,
        IReadOnlyList<PlannedMealSummary> alreadyPlanned,
        PlanningWeights weights,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProposedMeal>>([]);
}

internal sealed class NullPendingProposalStore : IPendingProposalStore
{
    public Task<IReadOnlyList<ProposedMeal>> GetAsync(string storeKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProposedMeal>>([]);
    public Task SetAsync(string storeKey, IReadOnlyList<ProposedMeal> proposals, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task RemoveAsync(string storeKey, DateOnly date, MealSlotId slotId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task ClearAsync(string storeKey, CancellationToken ct = default)
        => Task.CompletedTask;
}

internal sealed class NullPrefsRepo : IUserPreferenceRepository
{
    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<UserPreference?>(null);
    public Task AddAsync(UserPreference preference, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// ── P3-5 null stubs (no-op implementations for WAF factories that don't test insights) ────

internal sealed class NullExpiringStockReader : IMealPlanExpiringStockReader
{
    public Task<IReadOnlyList<Guid>> GetExpiringProductIdsAsync(
        DateOnly today, int withinDays, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>([]);
}

// ── P3-4 null stubs (no-op implementations for WAF factories that don't test enrichment) ────

internal sealed class NullStockReader : IMealPlanStockReader
{
    public Task<MealPlanProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<MealPlanProductStock?>(null);
}

internal sealed class NullPriceReader : IMealPlanPriceReader
{
    public Task<MealPlanPricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<MealPlanPricePoint?>(null);
}

// The former IMealPlanShoppingWriter stub is gone — ShopForWeekService now calls Shopping's
// AddItemCommand directly (intra-context since the Planning merge, ADR-024, plantry-g3da.5).
// WAF factories that don't exercise the shop-for-week write path stub its two dependencies instead:
// a repository with no list (so AddItemCommand fails fast if ever invoked — these factories never
// invoke ShopForWeekService.ExecuteAsync) and a catalog reader with no data.

internal sealed class NullShoppingListRepository : IShoppingListRepository
{
    public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NullShoppingCatalogReader : IShoppingCatalogReader
{
    public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, ShoppingProductSummary>>(new Dictionary<Guid, ShoppingProductSummary>());

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShoppingProductCandidate>>([]);

    public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default)
        => Task.FromResult<decimal?>(null);

    public Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShoppingUnitOption>>([]);

    public Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShoppingCategoryOption>>([]);
}

// ── plantry-0eut null stub (no-op cook status for WAF factories that don't test the Cook strip) ──

/// <summary>
/// Always-pending <see cref="IMealPlanCookStatusReader"/> — every dish resolves to absent (pending).
/// Registered wherever a WAF factory seeds real planned dishes but does not itself exercise the Cook
/// strip, so <see cref="MealPlanCookStatusReaderAdapter"/>'s real Recipes/Inventory DB dependency is
/// never constructed in these hermetic fragment tests (mirrors NullStockReader/NullPriceReader).
/// </summary>
internal sealed class NullCookStatusReader : IMealPlanCookStatusReader
{
    public Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, DishCookStatus>>(new Dictionary<Guid, DishCookStatus>());
}

// ── plantry-so5.3 null stubs (planning settings — no-op for WAF tests that don't test budget) ──

internal sealed class NullPlanningSettingsRepo : IHouseholdPlanningSettingsRepository
{
    public Task<HouseholdPlanningSettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<HouseholdPlanningSettings?>(null);

    public Task AddAsync(HouseholdPlanningSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NullWeekOverrideRepo : IWeekPlanningOverrideRepository
{
    public Task<WeekPlanningOverride?> FindAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<WeekPlanningOverride?>(null);

    public Task AddAsync(WeekPlanningOverride weekOverride, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// ── ADR-021 null stub (returns an empty WeekBag for WAF factories that don't test the read model) ──

internal sealed class NullWeekReadModel : IMealPlanWeekReadModel
{
    public Task<WeekBag> LoadAsync(
        IReadOnlyList<Guid> recipeIds,
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default)
        => Task.FromResult(new WeekBag(
            new Dictionary<Guid, RecipeFact>(),
            new Dictionary<Guid, IReadOnlyList<IngredientFact>>(),
            new Dictionary<Guid, ProductFact>(),
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            new Dictionary<Guid, UnitFact>(),
            new Dictionary<Guid, StockFact>(),
            new Dictionary<Guid, PriceFact>()));
}

// ── shared ITagReader stub (moved from MealPlanning/ConflictCellFragmentTests.cs) ──────────────

/// <summary>No-op ITagReader stub for WAF tests that don't test tag name resolution.</summary>
internal sealed class NullTagReader : ITagReader
{
    public Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TagGroup>>([]);
}

// ── shared UserManager stub (moved from Preferences/PreferencesOobFragmentTests.cs) ────────────

/// <summary>
/// Minimal UserManager stub that bypasses Identity infrastructure and always returns the fixture user.
/// </summary>
internal sealed class FakeUserManager(AppUser fixedUser)
    : UserManager<AppUser>(
        new FakeUserStore(),
        null!, null!, null!, null!, null!, null!, null!, null!)
{
    public override Task<AppUser?> GetUserAsync(ClaimsPrincipal principal) =>
        Task.FromResult<AppUser?>(fixedUser);
}

internal sealed class FakeUserStore : IUserStore<AppUser>
{
    public Task<IdentityResult> CreateAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
    public Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
    public void Dispose() { }
    public Task<AppUser?> FindByIdAsync(string userId, CancellationToken ct) => Task.FromResult<AppUser?>(null);
    public Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => Task.FromResult<AppUser?>(null);
    public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string> GetUserIdAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.UserName);
    public Task SetNormalizedUserNameAsync(AppUser user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;
    public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken ct) => Task.CompletedTask;
    public Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
}
