using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;

namespace Plantry.Tests.Unit.Shopping.Application;

/// <summary>
/// In-memory <see cref="IShoppingRecipeReader"/> for unit tests.
/// Recipe names are registered per recipe id; unregistered ids are omitted from results.
/// </summary>
internal sealed class FakeShoppingRecipeReader : IShoppingRecipeReader
{
    private readonly Dictionary<Guid, string> _names = [];

    public void RegisterRecipe(Guid recipeId, string name) =>
        _names[recipeId] = name;

    public Task<IReadOnlyDictionary<Guid, string>> GetRecipeNamesAsync(
        IReadOnlyList<Guid> recipeIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = recipeIds
            .Where(_names.ContainsKey)
            .ToDictionary(id => id, id => _names[id]);
        return Task.FromResult(result);
    }
}

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

/// <summary>
/// In-memory <see cref="IShoppingPantryReader"/> for unit tests.
/// Stock levels are registered per product id; unregistered products are omitted (as if never stocked).
/// </summary>
internal sealed class FakeShoppingPantryReader : IShoppingPantryReader
{
    private readonly Dictionary<Guid, ShoppingPantryStockLevel> _levels = [];

    public void RegisterStock(Guid productId, ShoppingPantryStockLevel level) =>
        _levels[productId] = level;

    public Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, ShoppingPantryStockLevel> result = productIds
            .Where(_levels.ContainsKey)
            .ToDictionary(id => id, id => _levels[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ShoppingPantryStockLevel>> GetLowStockProductsAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<ShoppingPantryStockLevel> result = _levels.Values
            .Where(l => l.IsLow)
            .ToList();
        return Task.FromResult(result);
    }
}

/// <summary>
/// Fake <see cref="IShoppingCatalogReader"/> for unit tests.
/// The conversion table is a dictionary keyed by (fromUnitId, toUnitId, productId) → converted amount,
/// populated per-test. All other methods return empty stubs.
/// </summary>
internal sealed class FakeShoppingCatalogReader : IShoppingCatalogReader
{
    private readonly Dictionary<(Guid from, Guid to, Guid product), decimal> _conversions = [];

    /// <summary>Registers a conversion outcome that TryConvertAsync will return.</summary>
    public void RegisterConversion(Guid fromUnitId, Guid toUnitId, Guid productId, decimal convertedAmount) =>
        _conversions[(fromUnitId, toUnitId, productId)] = convertedAmount;

    public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ShoppingProductSummary>>(new Dictionary<Guid, ShoppingProductSummary>());

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ShoppingProductCandidate>>([]);

    public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default)
    {
        decimal? result = _conversions.TryGetValue((fromUnitId, toUnitId, productId), out var converted)
            ? converted
            : null;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ShoppingUnitOption>>([]);

    public Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ShoppingCategoryOption>>([]);
}

/// <summary>
/// In-memory implementation of <see cref="IShoppingListRepository"/>.
/// One list per household (v1 invariant). Seeded by tests via <see cref="Seed"/>.
/// </summary>
internal sealed class FakeShoppingListRepository : IShoppingListRepository
{
    private readonly List<ShoppingList> _lists = [];
    public int SaveCalls { get; private set; }

    /// <summary>Inserts a pre-built list so tests can start from a known state.</summary>
    public void Seed(ShoppingList list) => _lists.Add(list);

    public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_lists.FirstOrDefault(l => l.HouseholdId == householdId));

    public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
        Task.FromResult(_lists.FirstOrDefault(l => l.Id == id));

    public Task AddAsync(ShoppingList list, CancellationToken ct = default)
    {
        _lists.Add(list);
        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        SaveCalls++;
        return Task.CompletedTask;
    }
}
