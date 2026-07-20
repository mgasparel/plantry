using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Inventory.Application;

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

/// <summary>In-memory <see cref="IProductStockRepository"/> keyed by (household, product). The
/// transaction scope runs inline — fakes have no real database to lock.</summary>
internal sealed class FakeProductStockRepository : IProductStockRepository
{
    public List<ProductStock> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }
    public int TransactionScopes { get; private set; }

    public Task<ProductStock?> FindForUpdateAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == householdId && s.ProductId == productId));

    public Task<ProductStock?> FindAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == householdId && s.ProductId == productId));

    public Task<ProductStock?> FindWithHistoryAsync(HouseholdId householdId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == householdId && s.ProductId == productId));

    public Task<List<ProductStock>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(s => s.HouseholdId == householdId).ToList());

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Items.Any(s => s.HouseholdId == householdId));

    public Task AddAsync(ProductStock stock, CancellationToken ct = default)
    {
        Items.Add(stock);
        return Task.CompletedTask;
    }

    public Task<bool> TryAddAndSaveAsync(ProductStock stock, CancellationToken ct = default)
    {
        var existing = Items.SingleOrDefault(s => s.HouseholdId == stock.HouseholdId && s.ProductId == stock.ProductId);
        if (existing is not null)
            return Task.FromResult(false);
        Items.Add(stock);
        SaveChangesCalls++;
        return Task.FromResult(true);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        TransactionScopes++;
        return await work(ct);
    }
}

/// <summary>Configurable stand-in for the Catalog read seam.</summary>
internal sealed class FakeCatalogReadFacade : ICatalogReadFacade
{
    public List<CatalogProductInfo> Products { get; } = [];
    public Dictionary<Guid, string> UnitCodes { get; } = [];
    public Dictionary<Guid, string> LocationNames { get; } = [];

    public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(Products.SingleOrDefault(p => p.Id == productId));

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<CatalogProductInfo>)Products.ToList());

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyDictionary<Guid, string>)UnitCodes);

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyDictionary<Guid, string>)LocationNames);
}

/// <summary>A clock pinned to a fixed instant so date-window tests can't straddle a day boundary.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Returns a fixed "expiring soon" horizon (defaults to the Inventory default of 7).</summary>
internal sealed class FakeExpiringSoonHorizon(int days = HouseholdInventorySettings.DefaultExpiringSoonDays) : IExpiringSoonHorizon
{
    public Task<int> GetDaysAsync(CancellationToken ct = default) => Task.FromResult(days);
}

/// <summary>In-memory <see cref="IHouseholdInventorySettingsRepository"/> keyed by household.</summary>
internal sealed class FakeHouseholdInventorySettingsRepository : IHouseholdInventorySettingsRepository
{
    public List<HouseholdInventorySettings> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<HouseholdInventorySettings?> FindByHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.HouseholdId == householdId));

    public Task AddAsync(HouseholdInventorySettings settings, CancellationToken ct = default)
    {
        Items.Add(settings);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>Hands out a single converter for every product.</summary>
internal sealed class FakeConversionProvider(IQuantityConverter converter) : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(converter);
}

/// <summary>Same-unit identity; any cross-unit pair passes through unchanged.</summary>
internal sealed class IdentityQuantityConverter : IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
}

/// <summary>Identity for same unit; otherwise multiplies by a configured per-pair factor, else fails.</summary>
internal sealed class FactorQuantityConverter(Dictionary<(Guid From, Guid To), decimal> factors) : IQuantityConverter
{
    public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId)
    {
        if (fromUnitId == toUnitId) return amount;
        if (factors.TryGetValue((fromUnitId, toUnitId), out var factor)) return amount * factor;
        return Error.Custom("Test.Unresolvable", $"no conversion from {fromUnitId} to {toUnitId}");
    }
}
