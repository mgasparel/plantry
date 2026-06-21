using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Builds deterministic fixture data for the L4 Browse page snapshot tests (P2-2c).
/// Three recipes exercise all render paths:
/// <list type="bullet">
///   <item>Cookable recipe "Pancakes": fully in-stock, "Cook tonight" flag, Vegetarian tag, known cost.</item>
///   <item>Soon recipe "Omelette": in-stock but ingredient expiring in 2 days → "Use soon" badge, Spicy tag, known cost.</item>
///   <item>NoCost recipe "Milk Shake": in-stock but no price data → cost cell omitted, no tag.</item>
/// </list>
/// GUIDs are scrubbed by Verify's ScrubInlineGuids() so random ids do not defeat the baselines;
/// no fixed ids are required.
/// </summary>
public static class RecipeBrowseFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdAId);
    private static readonly IClock Clock = SystemClock.Instance;

    // ── Product ids (fixed for deterministic stock/price maps) ────────────────
    public static readonly Guid FlourId  = Guid.Parse("11111111-1111-1111-1111-111111111101");
    public static readonly Guid EggId    = Guid.Parse("11111111-1111-1111-1111-111111111102");
    public static readonly Guid MilkId   = Guid.Parse("11111111-1111-1111-1111-111111111103");
    public static readonly Guid GramUnit = Guid.Parse("11111111-1111-1111-1111-111111111110");
    public static readonly Guid EachUnit = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ── Tag ids (fixed so tag assignments are deterministic) ──────────────────
    public static readonly TagId VegTagId   = new(Guid.Parse("22222222-2222-2222-2222-222222222201"));
    public static readonly TagId SpicyTagId = new(Guid.Parse("22222222-2222-2222-2222-222222222202"));

    // Recipes are built fresh per test run; their ids are random (Verify scrubs all GUIDs).
    public static IReadOnlyList<Recipe> BuildRecipes()
    {
        // Pancakes: flour, in-stock, Vegetarian tag.
        var pancakes = Recipe.Create(Household, "Pancakes", defaultServings: 4, Clock).Value;
        pancakes.SetCookTime(20, Clock);
        pancakes.SetTags([VegTagId], Clock);
        pancakes.ReplaceIngredients(
            [new IngredientLine(FlourId, 200m, GramUnit, null, 0)], Clock);

        // Omelette: eggs expiring soon, Spicy tag.
        var omelette = Recipe.Create(Household, "Omelette", defaultServings: 2, Clock).Value;
        omelette.SetCookTime(10, Clock);
        omelette.SetTags([SpicyTagId], Clock);
        omelette.ReplaceIngredients(
            [new IngredientLine(EggId, 3m, EachUnit, null, 0)], Clock);

        // Milk Shake: milk, no price data → cost omitted.
        var milkShake = Recipe.Create(Household, "Milk Shake", defaultServings: 2, Clock).Value;
        milkShake.SetCookTime(5, Clock);
        milkShake.ReplaceIngredients(
            [new IngredientLine(MilkId, 300m, GramUnit, null, 0)], Clock);

        return [pancakes, omelette, milkShake];
    }

    // ── Catalog products ──────────────────────────────────────────────────────

    public static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [FlourId] = new(FlourId, "Flour", TrackStock: true, GramUnit, null, false, []),
            [EggId]   = new(EggId,   "Eggs",  TrackStock: true, EachUnit, null, false, []),
            [MilkId]  = new(MilkId,  "Milk",  TrackStock: true, GramUnit, null, false, []),
        };

    // ── Stock ─────────────────────────────────────────────────────────────────
    // Pancakes (Flour): 500g available, no expiry → InStock, not soon.
    // Omelette (Eggs):  10 eggs available, expiring 2026-06-16 (2 days out from today=14) → soon.
    // MilkShake (Milk): 500g available, no expiry → InStock, not soon.

    public static IReadOnlyDictionary<Guid, ProductStock> Stock() =>
        new Dictionary<Guid, ProductStock>
        {
            [FlourId] = new(FlourId, 500m, GramUnit, SoonestExpiry: null),
            [EggId]   = new(EggId,   10m,  EachUnit, SoonestExpiry: new DateOnly(2026, 6, 16)),
            [MilkId]  = new(MilkId,  500m, GramUnit, SoonestExpiry: null),
        };

    // ── Prices ────────────────────────────────────────────────────────────────
    // Flour: $2/kg → $0.002/g → 200g * $0.002 = $0.40 → cost/serving = $0.10/serv × 4 servings.
    // Eggs: $3.50/12 = $0.292/ea → 3 × $0.292 = $0.875 → cost/serving = $0.4375/serv × 2 servings.
    // Milk: no price → CostCompleteness.None → omit cost.

    public static IReadOnlyDictionary<Guid, PricePoint> Prices() =>
        new Dictionary<Guid, PricePoint>
        {
            [FlourId] = new(FlourId, Price: 2.00m, Quantity: 1000m, UnitId: GramUnit, UnitPrice: 0.002m),
            [EggId]   = new(EggId,   Price: 3.50m, Quantity: 12m,   UnitId: EachUnit, UnitPrice: null),
            // MilkId intentionally omitted.
        };

    // ── Tags ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<Tag> Tags() => BuildTags();

    private static IReadOnlyList<Tag> BuildTags()
    {
        var veg   = Tag.Create(Household, "Vegetarian", null, Clock);
        var spicy = Tag.Create(Household, "Spicy", null, Clock);
        // Patch ids so tag membership lookups (by TagId) work correctly in the fixture.
        SetTagId(veg, VegTagId);
        SetTagId(spicy, SpicyTagId);
        return [veg, spicy];
    }

    private static void SetTagId(Tag tag, TagId id)
    {
        // Tag.Id is { get; protected set; } via Entity<TagId>. Use reflection (test-only).
        var prop = typeof(Entity<TagId>)
            .GetProperty(nameof(tag.Id), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(tag, id);
    }
}

// ── Browse-specific repository fakes ──────────────────────────────────────────────────────────────

/// <summary>
/// In-memory <see cref="IRecipeRepository"/> for the Browse page L4 tests. Returns the fixture
/// recipes for the owning household; only <see cref="ListForBrowseAsync"/> is exercised by browse.
/// </summary>
public sealed class FakeBrowseRecipeRepository(ITenantContext tenant, IReadOnlyList<Recipe> recipes)
    : IRecipeRepository
{
    public Task AddAsync(Recipe r, CancellationToken ct = default) => Task.CompletedTask;

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } hid) return Task.FromResult<Recipe?>(null);
        return Task.FromResult(recipes.FirstOrDefault(r => r.HouseholdId.Value == hid && r.Id == id));
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } hid) return Task.FromResult<IReadOnlyList<Recipe>>([]);
        IReadOnlyList<Recipe> result = recipes.Where(r => r.HouseholdId.Value == hid).ToList();
        return Task.FromResult(result);
    }

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(recipes.Any(r => r.HouseholdId == householdId && r.ArchivedAt == null));
}

/// <summary>
/// In-memory <see cref="ITagRepository"/> for Browse tests. Returns fixture tags for
/// <see cref="ListAllAsync"/>; resolves names for tag id → name lookups.
/// </summary>
public sealed class FakeBrowseTagRepository(IReadOnlyList<Tag> tags) : ITagRepository
{
    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult<Tag?>(null);

    public Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(
        IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
        IReadOnlyDictionary<TagId, string> result = ids
            .Where(id => tags.Any(t => t.Id == id))
            .ToDictionary(id => id, id => tags.First(t => t.Id == id).Name);
        return Task.FromResult(result);
    }

    public Task AddAsync(Tag tag, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Tag>>(tags.ToList());
}

/// <summary>
/// In-memory <see cref="IInventoryStockReader"/> for Browse tests.
/// </summary>
public sealed class FakeBrowseStockReader(IReadOnlyDictionary<Guid, ProductStock> stock)
    : IInventoryStockReader
{
    public Task<ProductStock?> FindStockAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(stock.GetValueOrDefault(productId));

    public Task<IReadOnlyDictionary<Guid, ProductStock>> FindStockBatchAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, ProductStock> result = productIds
            .Where(stock.ContainsKey)
            .ToDictionary(id => id, id => stock[id]);
        return Task.FromResult(result);
    }
}

/// <summary>
/// In-memory <see cref="IPriceReader"/> for Browse tests.
/// </summary>
public sealed class FakeBrowsePriceReader(IReadOnlyDictionary<Guid, PricePoint> prices) : IPriceReader
{
    public Task<PricePoint?> FindLatestAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(prices.GetValueOrDefault(productId));
}

/// <summary>
/// Identity <see cref="IUnitConverter"/> — same-unit converts to itself. Sufficient for fixture where
/// ingredient unit == product default unit.
/// </summary>
public sealed class FakeBrowseUnitConverter : IUnitConverter
{
    public Task<Plantry.SharedKernel.Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
        Task.FromResult(fromUnitId == toUnitId
            ? Plantry.SharedKernel.Result<decimal>.Success(amount)
            : Plantry.SharedKernel.Result<decimal>.Failure(
                Plantry.SharedKernel.Error.Custom("Test.NoPath", "No unit conversion path.")));
}
