using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Deals.Application;

internal sealed class FakeStoreSubscriptionRepository : IStoreSubscriptionRepository
{
    public List<StoreSubscription> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<StoreSubscription?> FindAsync(StoreSubscriptionId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.Id == id));

    public Task<StoreSubscription?> FindByStoreAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.StoreId == storeId));

    public Task<List<StoreSubscription>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.OrderBy(s => s.CreatedAt).ToList());

    public Task AddAsync(StoreSubscription subscription, CancellationToken ct = default)
    {
        Items.Add(subscription);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fake ICatalogStoreWriter that mimics Catalog's ensure-by-external-identity: one stable store id per
/// external_ref, idempotent across calls. Records the ensured merchants for assertions.
/// </summary>
internal sealed class FakeCatalogStoreWriter : ICatalogStoreWriter
{
    private readonly Dictionary<string, Guid> _byRef = new(StringComparer.Ordinal);
    public List<(string ExternalRef, string Name)> EnsureCalls { get; } = [];

    public Task<Guid> EnsureAsync(string externalRef, string name, CancellationToken ct = default)
    {
        EnsureCalls.Add((externalRef, name));
        if (!_byRef.TryGetValue(externalRef, out var id))
        {
            id = Guid.NewGuid();
            _byRef[externalRef] = id;
        }
        return Task.FromResult(id);
    }
}

internal sealed class FakeCatalogStoreReader : ICatalogStoreReader
{
    public Dictionary<Guid, string> Names { get; } = new();

    public Task<CatalogStoreInfo?> FindAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(Names.TryGetValue(storeId, out var n)
            ? new CatalogStoreInfo(storeId, n, null)
            : null);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = storeIds
            .Where(Names.ContainsKey)
            .ToDictionary(id => id, id => Names[id]);
        return Task.FromResult(result);
    }
}

internal sealed class FakeFlyerSource : IFlyerSource
{
    public List<DirectoryMerchant> Merchants { get; } =
    [
        new("flipp-freshco", "FreshCo"),
        new("flipp-metro", "Metro"),
    ];

    public Task<IReadOnlyList<DirectoryMerchant>> SearchDirectoryAsync(
        string postalCode, string? nameQuery, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
            return Task.FromResult<IReadOnlyList<DirectoryMerchant>>([]);

        IReadOnlyList<DirectoryMerchant> results = string.IsNullOrWhiteSpace(nameQuery)
            ? Merchants
            : Merchants.Where(m => m.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(results);
    }

    public Task<FlyerPullResult> PullFlyerAsync(
        string externalRef, string postalCode, CancellationToken ct = default) =>
        throw new NotSupportedException("Fake flyer source does not pull.");
}

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}
